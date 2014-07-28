
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
            var nugetApiKeyArguments = new[] { "apikey", "api", "a", "key", "k" };

            //these values are given as arguments.
            var githubUsername = string.Empty;
            var nugetApiKey = string.Empty;

            //for testing purposes, if there is a nuget.apikey file, load the key from there per default to make it all easier.
            if (File.Exists("nuget.apikey"))
            {
                nugetApiKey = File.ReadAllText("nuget.apikey");
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
                else if (nugetApiKeyArguments.Contains(normalizedArguments[i]) && !isLastArgument)
                {
                    nugetApiKey = args[++i];
                }

            }

            //check parameters.
            if (string.IsNullOrEmpty(nugetApiKey) ||string.IsNullOrEmpty(githubUsername))
            {
                Console.Error.WriteLine("Some required arguments are missing.");

                DisplayHelp();
                return;
            }

            Console.WriteLine("Configuring NuGet ...");

            //deploy nuget.
            File.WriteAllBytes("nuget.exe", Resources.NuGet);

            //auto-update nuget.
            RunCommandLine("nuget", "update -self");

            //set the api key.
            RunCommandLine("nuget", "setApiKey " + nugetApiKey);

            //instantiate github api wrapper.
            var product = new ProductHeaderValue("GithubNugetDistributor");
            var githubClient = new GitHubClient(product);

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

                Console.WriteLine("Cloning " + repository.CloneUrl + " ...");
                
                //run a git clone on the repository into the clone path.
                RunCommandLine("git", "clone " + repository.CloneUrl + " \"" + clonePath + "\"");

                //whether or not this project should be turned into a nuget package.
                var assemblyInformationPath = string.Empty;
                var included = false;

                //remove items that should never be included in a package, and check wether or not this is a real C# project.
                var files = Directory.GetFiles(clonePath, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {

                    var fileName = Path.GetFileName(file);
                    var fileExtension = Path.GetExtension(file);
                    var directoryName = Path.GetFileName(Path.GetDirectoryName(file));

                    if (fileExtension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        //is this a C# project? if so, count this project in.
                        included = true;
                    }
                    else if (fileExtension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        //is it a .cs file instead? see if it is the assembly info file which should not be included.
                        if (fileName.Equals("AssemblyInfo.cs") && directoryName.Equals("Properties"))
                        {
                            assemblyInformationPath = file;
                        }
                    }
                    
                    //see if this is a type of file that should be deleted, and delete it instantly.
                    var deletedFileExtensions = new[] { ".csproj", ".gitignore", ".sln" };
                    if(deletedFileExtensions.Contains(fileExtension)) {
                        File.Delete(file);
                    }

                }

                //was it a proper project to include in a package?
                if (included)
                {

                    Console.WriteLine("C# project found, creating NuGet package of " + repository.Name + ".");

                    //delete the assembly information file.
                    if(!string.IsNullOrEmpty(assemblyInformationPath)) {
                        File.Delete(assemblyInformationPath);
                    }

                    //fetch a list of the user's commits.
                    var commits = await githubClient.Repository.Commits.GetAll(user.Login, repository.Name);

                    //fetch version number.
                    var version = commits.Count;

                    //fetch a brand new nuspec file from the template.
                    var nuspecFileContents = string.Format(Resources.NuGetPackage, repository.Name, version, user.Name ?? user.Login, repository.Description, DateTime.UtcNow.Year);

                    //get file path for the new nuspec file.
                    var nuspecFilePath = Path.Combine(packagePath, "Package.nuspec");

                    //write the nuspec file.
                    File.WriteAllText(nuspecFilePath, nuspecFileContents);

                    //create the nuget package.
                    RunCommandLine("nuget", "pack \"" + nuspecFilePath + "\"");

                    //push the nuget package.
                    RunCommandLine("nuget", "push " + repository.Name + ".1.0." + version + ".nupkg");

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

        private static void RunCommandLine(string command, string arguments)
        {
            var information = new ProcessStartInfo(command);
            information.Arguments = arguments;
            information.WorkingDirectory = Environment.CurrentDirectory;

            using (var process = Process.Start(information))
            {
                process.WaitForExit();
            }
        }

        private static void DisplayHelp()
        {
            using (var process = Process.GetCurrentProcess())
            {
                Console.WriteLine("Usage: " + process.ProcessName + ".exe -user <GitHub username> -apikey <nuget API key> [additional arguments]");
            }
        }
    }
}
