
using System.Reflection;
using System.Security.Principal;
using GithubNugetDistributor.Properties;
using Octokit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Octokit.Internal;

namespace GithubNugetDistributor
{
    class Program
    {
        static void Main(string[] args)
        {

            var task = Run(args);
            task.Wait();

            //if we are running this from visual studio, don't exit instantly.
            if (Debugger.IsAttached)
            {
                Console.WriteLine();
                Console.WriteLine("Done executing.");

                Console.ReadLine();
            }
        }

        private static async Task Run(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.White;

            Console.Clear();

            Console.WriteLine("This program allows you to (given a Github username) fetch all the projects from that user and automatically package them as NuGet packages, and upload them too. Make sure you have Git installed before you continue.");
            Console.WriteLine();

            //if we don't have enough arguments, display help and exit.
            if (!args.Any())
            {
                DisplayHelp();
                return;
            }

            //normalize all arguments.
            var normalizedArguments = args
                .Select(a => a
                    .Replace("-", "")
                    .Replace("/", "")
                    .ToLowerInvariant())
                .ToArray();

            //display the help page if needed.
            var helpArguments = new[] { "help", "h", "?" };
            var githubUsernameArguments = new[] { "user", "u" };
            var githubPasswordArguments = new[] { "password", "p" };
            var nugetApiKeyArguments = new[] { "apikey", "api", "a", "key", "k" };

            //these values are given as arguments.
            var githubUsername = string.Empty;
            var githubPassword = string.Empty;
            var nugetApiKey = string.Empty;

            //for testing purposes, if there are files for credentials and keys, load the key from there per default to make it all easier.
            if (File.Exists("nuget.apikey"))
            {
                nugetApiKey = File.ReadAllText("nuget.apikey");
            }
            if (File.Exists("github.password"))
            {
                githubPassword = File.ReadAllText("github.password");
            }

            //go through all arguments and set values as needed.
            for (var i = 0; i < args.Length; i++)
            {

                var isLastArgument = i == args.Length - 1;

                if (helpArguments.Contains(normalizedArguments[i]))
                {
                    DisplayHelp();
                    return;

                }
                else if (githubUsernameArguments.Contains(normalizedArguments[i]) && !isLastArgument)
                {
                    githubUsername = args[++i];
                }
                else if (githubPasswordArguments.Contains(normalizedArguments[i]) && !isLastArgument)
                {
                    githubPassword = args[++i];
                }
                else if (nugetApiKeyArguments.Contains(normalizedArguments[i]) && !isLastArgument)
                {
                    nugetApiKey = args[++i];
                }

            }

            //check parameters.
            if (string.IsNullOrEmpty(nugetApiKey) || string.IsNullOrEmpty(githubUsername))
            {
                Console.Error.WriteLine("Some required arguments are missing.");

                DisplayHelp();
                return;
            }

            Console.WriteLine("Configuring NuGet ...");

            //deploy nuget.
            File.WriteAllBytes("nuget.exe", Resources.NuGet);

            //auto-update nuget.
            RunCommandLine("nuget", "update -self", false);

            //set the api key.
            RunCommandLine("nuget", "setApiKey " + nugetApiKey, false);

            //instantiate github api wrapper.
            var product = new ProductHeaderValue("GithubNugetDistributor");

            GitHubClient githubClient;
            if (!string.IsNullOrEmpty(githubPassword))
            {
                githubClient = new GitHubClient(product,
                    new InMemoryCredentialStore(new Credentials(githubUsername, githubPassword)));
            }
            else
            {
                githubClient = new GitHubClient(product);
            }

            //now let's sign in to github and make sure that the username is correct.
            var user = await githubClient.User.Get(githubUsername);
            if (user == null)
            {
                Console.Error.WriteLine("The given GitHub user does not exist.");
                return;
            }

            Console.WriteLine("Cloning repositories ...");

            //go through every repository.
            var repositories = await githubClient.Repository.GetAllForUser(user.Login);
            foreach (var repository in repositories.Where(r => !r.Fork))
            {
                var packagePath = Path.Combine("Repositories", repository.Name);
                var clonePath = Path.Combine(packagePath, "content", repository.Name);

                //create a subdirectory for all repositories.
                if (!TryCreateDirectory(clonePath)) return;

                Console.WriteLine();
                Console.WriteLine("Cloning " + repository.CloneUrl + " ...");

                //run a git clone on the repository into the clone path.
                RunCommandLine("git", "clone " + repository.CloneUrl + " \"" + clonePath + "\"", true);

                //remove items that should never be included in a package, and check wether or not this is a real C# project.
                var files = Directory.GetFiles(clonePath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {

                    var fileExtension = Path.GetExtension(file);
                    Debug.Assert(fileExtension != null, "fileExtension != null");

                    if (fileExtension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || fileExtension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                    {

                        //get directory structure information.
                        var projectFileNameWithoutExtension = Path.GetFileNameWithoutExtension(file);
                        var projectDirectoryPath = Path.GetDirectoryName(file);

                        //is this a C# or VB project? if so, count this project in.
                        Console.WriteLine(" - Project found.");

                        //compile the project.
                        Console.WriteLine(" - Compiling project ...");

                        //first we need to find the latest msbuild file.
                        var frameworkDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework" + (Environment.Is64BitOperatingSystem ? "64" : ""));

                        //get the latest framework version folder. all the framework folders are in the format "vX.X.X" where X.X.X is what we are looking for (the version number).
                        var frameworkFolders = Directory.GetDirectories(frameworkDirectory, "*", SearchOption.TopDirectoryOnly)
                            .Select(d => new DirectoryInfo(d));

                        var frameworkVersions = frameworkFolders
                            .Where(f => f.Name.StartsWith("v"))
                            .Select(f => f.Name.Substring(1))
                            .Select(f => f.Split('.'));

                        var latestFrameworkVersion = "v" + frameworkVersions
                            .OrderByDescending(v => v.Length > 0 ? v[0] : "0")
                            .ThenByDescending(v => v.Length > 1 ? v[1] : "0")
                            .ThenByDescending(v => v.Length > 2 ? v[2] : "0")
                            .First()
                            .Aggregate((a, b) => a + "." + b);

                        //now we know the MSBuild path of the latest version of the .net framework.
                        var msbuildPath = Path.Combine(frameworkDirectory, latestFrameworkVersion + "", "msbuild");

                        //and finally now use msbuild to compile.
                        RunCommandLine(msbuildPath, "\"" + file + "\"", true);

                        //create a nuget package.
                        var packageName = repository.Name + "." + projectFileNameWithoutExtension;
                        Console.WriteLine(" - Creating NuGet package " + packageName + " ...");

                        //fetch a list of the user's commits.
                        var commits = await githubClient.Repository.Commits.GetAll(user.Login, repository.Name);

                        //set the package version number to be the amount of commits.
                        var version = commits.Count;

                        //fetch a brand new nuspec file from the template.
                        var nuspecFileContents = string.Format(Resources.NuGetPackage, packageName, version, user.Name ?? user.Login, repository.Description, DateTime.UtcNow.Year, repository.HtmlUrl);

                        //get file path for the new nuspec file.
                        Debug.Assert(projectDirectoryPath != null, "projectDirectoryPath != null");
                        var nuspecFilePath = Path.Combine(projectDirectoryPath, projectFileNameWithoutExtension + ".nuspec");

                        //write the nuspec file.
                        File.WriteAllText(nuspecFilePath, nuspecFileContents);

                        //create the nuget package.
                        RunCommandLine("nuget", "pack \"" + file + "\" -IncludeReferencedProjects -OutputDirectory \"" + packagePath + "\"", true);

                        //push the nuget package.
                        var packageFilePath = Path.Combine(packagePath, packageName + ".1.0." + version + ".nupkg");
                        RunCommandLine("nuget", "push \"" + packageFilePath + "\"", true);
                    }

                }

            }

        }

        private static bool TryCreateDirectory(string clonePath)
        {
            try
            {
                if (Directory.Exists(clonePath))
                {
                    Directory.Delete(clonePath, true);
                }

                Directory.CreateDirectory(clonePath);

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Could not create or delete the '" + clonePath + "' subfolder since access was denied. Are you trying to run the program from a User Account Control protected folder?");
            }
            catch (PathTooLongException)
            {
                Console.Error.WriteLine("Could not create or delete the '" + clonePath + "' subfolder since the path was too long.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not create or delete the '" + clonePath + "' due to an unknown reason. " + ex.Message);
            }

            return false;
        }

        private static void RunCommandLine(string command, string arguments, bool redirectOutput)
        {
            var information = new ProcessStartInfo(command);
            information.Arguments = arguments;
            information.WorkingDirectory = Environment.CurrentDirectory;

            information.RedirectStandardError = redirectOutput;
            information.RedirectStandardOutput = redirectOutput;

            information.CreateNoWindow = redirectOutput;
            information.UseShellExecute = !redirectOutput;

            using (var process = Process.Start(information))
            {
                if (process == null) return;

                if (redirectOutput)
                {
                    process.ErrorDataReceived += process_ErrorDataReceived;
                    process.OutputDataReceived += process_OutputDataReceived;

                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();
                }

                process.WaitForExit();
            }
        }

        static void process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine(e.Data);
            Console.ForegroundColor = ConsoleColor.White;
        }

        static void process_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(e.Data);
            Console.ForegroundColor = ConsoleColor.White;
        }

        private static void DisplayHelp()
        {
            using (var process = Process.GetCurrentProcess())
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("\t" + process.ProcessName + ".exe -user <GitHub username> -apikey <nuget API key> [-password <GitHub password>]");
                Console.WriteLine();
                Console.WriteLine("Note: Authenticating with a GitHub password may be nescessary to get additional API requests.");

            }
        }
    }
}
