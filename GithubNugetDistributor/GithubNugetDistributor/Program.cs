
using Octokit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GithubNugetDistributor
{
    class Program
    {
        static void Main(string[] args)
        {
            var task = Start(args);
            task.Wait();

            //if we are running this from visual studio, don't exit instantly.
            if (Debugger.IsAttached)
            {
                Console.ReadLine();
            }
        }

        static async Task Start(string[] args)
        {
            Console.WriteLine("Welcome to the Github Nuget distributor. This program allows you to (given a Github username) fetch all the projects from that user and automatically package them as NuGet packages, and upload them too. Make sure you have Git installed before you continue.");
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
            var userArguments = new[] { "user", "u" };

            //per default, no user is specified and it should ask later.
            var githubUsername = string.Empty;

            //go through all arguments.
            for (var i = 0; i < args.Length; i++)
            {

                var isFirst = i == 0;
                var isLast = i == args.Length - 1;

                //display help page if needed.
                if (helpArguments.Contains(normalizedArguments[i]))
                {

                    DisplayHelp();
                    return;

                }
                else if (userArguments.Contains(normalizedArguments[i]) && !isLast)
                {

                    githubUsername = args[i + 1];

                }
                else if (isFirst)
                {

                    //assume that the argument given is the username.
                    githubUsername = args[i];

                }

            }

            //instantiate github api wrapper.
            var product = new ProductHeaderValue("GithubNugetDistributor");
            var githubClient = new GitHubClient(product);

            //now let's sign in to github and make sure that the username is correct.
            var user = await githubClient.User.Get(githubUsername);
            if (user == null)
            {
                Console.Error.WriteLine("The given user does not exist.");
                return;
            }

            Console.WriteLine("Fetching repositories ...");

            //go through every repository.
            var repositories = await githubClient.Repository.GetAllForUser(user.Login);
            foreach (var repository in repositories)
            {
                var clonePath = "Repositories\\" + repository.Name;

                //create a subdirectory for all repositories.

                try
                {
                    if (Directory.Exists(clonePath))
                    {
                        Directory.Delete(clonePath, true);
                    }

                    Directory.CreateDirectory(clonePath);
                }
                catch (UnauthorizedAccessException)
                {
                    Console.Error.WriteLine("Could not create or delete the '" + clonePath + "' subfolder since access was denied. Are you trying to run the program from a User Account Control protected folder?");
                    return;
                }
                catch (PathTooLongException)
                {
                    Console.Error.WriteLine("Could not create or delete the '" + clonePath + "' subfolder since the path was too long.");
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Could not create or delete the '" + clonePath + "' due to an unknown reason. " + ex.Message);
                    return;
                }

                Console.WriteLine("Cloning " + repository.CloneUrl + " ...");
                
                //run a git clone on the repository into the clone path.
                RunCommandLine("git", "clone " + repository.CloneUrl + " " + clonePath);

            }

        }

        static void RunCommandLine(string command, string arguments)
        {
            var information = new ProcessStartInfo(command);
            information.Arguments = arguments;
            information.WorkingDirectory = Environment.CurrentDirectory;

            using (var process = Process.Start(information))
            {
                process.WaitForExit();
            }
        }

        static void DisplayHelp()
        {
            using (var process = Process.GetCurrentProcess())
            {
                Console.WriteLine("Usage: " + process.ProcessName + ".exe <github username> [additional arguments]");
            }
        }
    }
}
