using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.DocFX.DocFXTasks;
using System.Text.Json;
using System.IO;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using Nuke.Common.ChangeLog;
using System.Collections.Generic;
using Nuke.Common.Tools.GitHub;
using Nuke.Common.Utilities;

[CheckBuildProjectConfigurations]
[ShutdownDotNetAfterServerBuild]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.CreatePackage);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(Framework = "net6.0")] readonly GitVersion GitVersion;

    [Parameter] string NugetPrerelease;

    // Directories
    AbsolutePath ToolsDir => RootDirectory / "tools";
    AbsolutePath Output => RootDirectory / "bin";
    AbsolutePath OutputNuget => Output / "nuget";
    AbsolutePath OutputTests => RootDirectory / "TestResults";
    AbsolutePath OutputPerfTests => RootDirectory / "PerfResults";
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath DocSiteDirectory => RootDirectory / "docs/_site";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    public string ChangelogFile => RootDirectory / "CHANGELOG.md";
    public AbsolutePath DocFxDir => RootDirectory / "docs";
    public string DocFxDirJson => DocFxDir / "docfx.json";


    [Parameter] readonly string Source = "https://resharper-plugins.jetbrains.com/api/v2/package";

    static readonly JsonElement? _githubContext = string.IsNullOrWhiteSpace(EnvironmentInfo.GetVariable<string>("GITHUB_CONTEXT")) ? 
        null 
        : JsonSerializer.Deserialize<JsonElement>(EnvironmentInfo.GetVariable<string>("GITHUB_CONTEXT"));

    //let hasTeamCity = (not (buildNumber = "0")) // check if we have the TeamCity environment variable for build # set
    static readonly int BuildNumber = _githubContext.HasValue ? int.Parse(_githubContext.Value.GetProperty("run_number").GetString()) : 0;
        
    public ChangeLog Changelog => ReadChangelog(ChangelogFile);

    public ReleaseNotes LatestVersion => Changelog.ReleaseNotes.OrderByDescending(s => s.Version).FirstOrDefault() ?? throw new ArgumentException("Bad Changelog File. Version Should Exist");
    public string ReleaseVersion => LatestVersion.Version?.ToString() ?? throw new ArgumentException("Bad Changelog File. Define at least one version");

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            RootDirectory
            .GlobDirectories("src/**/bin", "src/**/obj", Output, OutputTests, OutputPerfTests, OutputNuget, DocSiteDirectory)
            .ForEach(DeleteDirectory);
            EnsureCleanDirectory(Output);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });
    IEnumerable<string> ChangelogSectionNotes => ExtractChangelogSectionNotes(ChangelogFile);

    Target RunChangelog => _ => _
        //.OnlyWhenStatic(() => InvokedTargets.Contains(nameof(RunChangelog)))
        .Executes(() =>
        {
            // GitVersion.SemVer appends the branch name to the version numer (this is good for pre-releases)
            // If you are executing this under a branch that is not beta or alpha - that is final release branch
            // you can update the switch block to reflect your release branch name
            var vNext = string.Empty;
            var branch = GitVersion.BranchName;
            switch (branch)
            {
                case "main":
                case "master":
                    vNext = GitVersion.MajorMinorPatch;
                    break;
                default:
                    vNext = GitVersion.SemVer;  
                    break;
            }
            FinalizeChangelog(ChangelogFile, vNext, GitRepository);

            Git($"add {ChangelogFile}");
            Git($"commit -m \"Finalize {Path.GetFileName(ChangelogFile)} for {vNext}.\"");

            //To sign your commit
            //Git($"commit -S -m \"Finalize {Path.GetFileName(ChangelogFile)} for {GitVersion.SemVer}.\"");

            Git($"tag -f {GitVersion.SemVer}");
        });
    Target CreatePackage => _ => _
      .DependsOn(RunTests)
      .Executes(() =>
      {
          var version = LatestVersion;
          var projects = Solution.Projects
          .Where(x => !x.Name.Contains("Test") && !x.Name.Contains("_build"));
          foreach(var project in projects)
          {
              DotNetPack(s => s
                  .SetProject(project)
                  .SetConfiguration(Configuration)
                  .EnableNoBuild()

                  .EnableNoRestore()
                  .SetAssemblyVersion(version.Version.ToString())
                  .SetFileVersion(version.Version.ToString())
                  .SetVersion(version.Version.ToString())
                  .SetIncludeSymbols(true)  
                  .SetPackageReleaseNotes(GetNuGetReleaseNotes(ChangelogFile, GitRepository))
                  .SetDescription("YOUR_DESCRIPTION_HERE")
                  .SetPackageProjectUrl("YOUR_PACKAGE_URL_HERE")
                  .SetOutputDirectory(OutputNuget)); 
          }
      });
    Target RunTests => _ => _
        .After(Compile)
        .Executes(() =>
        {
            var projects = Solution.GetProjects("*.Tests");
            foreach(var project in projects)
            {
                Information($"Running tests from {project}");
                foreach (var fw in project.GetTargetFrameworks())
                {
                    Information($"Running for {project} ({fw}) ...");
                    DotNetTest(c => c
                           .SetProjectFile(project)
                           .SetConfiguration(Configuration.ToString())
                           .SetFramework(fw)
                           .SetResultsDirectory(OutputTests)
                           .SetProcessWorkingDirectory(Directory.GetParent(project).FullName) 
                           .SetLoggers("trx")
                           .SetVerbosity(verbosity: DotNetVerbosity.Normal)
                           .EnableNoBuild());
                }
            }
        });
    Target Docker => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });
    //--------------------------------------------------------------------------------
    // Documentation 
    //--------------------------------------------------------------------------------
    Target CreateMetadata => _ => _
        .DependsOn(Compile)
        .Executes(() => 
        {
            DocFX($"metadata {DocFxDirJson}");
        });

    Target DocFx => _ => _
        .DependsOn(CreateMetadata)
        .Executes(() => 
        {
            DocFX($"build {DocFxDirJson}", workingDirectory: DocFxDir, timeout: TimeSpan.FromMinutes(30).Minutes);
        });

    Target ServeDocs => _ => _
        .DependsOn(DocFx)
        .Executes(() => DocFX($"{DocFxDirJson} --serve"));

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            var version = LatestVersion;
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(version.Version.ToString())
                .SetFileVersion(version.Version.ToString())
                .EnableNoRestore());
        });

    Target Install => _ => _
        //.DependsOn<IPack>()
        .Executes(() =>
        {
            DotNet($"tool install Nuke.GlobalTool --global");
        });


    static void Information(string info)
    {
        Serilog.Log.Information(info);
    }
}
