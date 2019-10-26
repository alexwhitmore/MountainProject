﻿using MountainProjectAPI;
using MountainProjectBot;
using Newtonsoft.Json.Linq;
using RedditSharp;
using RedditSharp.Things;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static MountainProjectBot.Enums;

/* =============================================================
 * 
 * 
 * This console app exists only temporarily as sort of a "testing
 * phase" until I am more confident in the results of the
 * ParsePostTitleToRoute function 
 * 
 * 
 * =============================================================
 */

namespace MountainProjectBot_AutoReply
{
    class Program
    {
        const string XMLNAME = "MountainProjectAreas.xml";
        static string xmlPath = Path.Combine(@"..\..\MountainProjectDBBuilder\bin\", XMLNAME);
        const string CREDENTIALSNAME = @"..\..\MountainProjectBot\Credentials.txt";
        static string credentialsPath = Path.Combine(@"..\", CREDENTIALSNAME);
        static string repliedToPostsPath = @"RepliedToPosts.txt";
        static string blacklistedPath = @"..\..\MountainProjectBot\bin\BlacklistedUsers.txt";
        static string requestForApprovalURL = "";

        static RedditHelper redditHelper = new RedditHelper();

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            CheckRequiredFiles();
            MountainProjectDataSearch.InitMountainProjectData(xmlPath);
            redditHelper.Auth(credentialsPath).Wait();
            GetRequestServerURL(credentialsPath);
            DoBotLoop().Wait();
        }

        private static void GetRequestServerURL(string filePath)
        {
            List<string> fileLines = File.ReadAllLines(filePath).ToList();
            requestForApprovalURL = fileLines.FirstOrDefault(p => p.Contains("requestForApprovalURL")).Split(new[] { ':' }, 2)[1]; //Split on first occurence only because requestForApprovalURL also contains ':'
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;

            string exceptionString = "";
            exceptionString += $"[{DateTime.Now}] EXCEPTION TYPE: {ex?.GetType()}" + Environment.NewLine + Environment.NewLine;
            exceptionString += $"[{DateTime.Now}] EXCEPTION MESSAGE: {ex?.Message}" + Environment.NewLine + Environment.NewLine;
            exceptionString += $"[{DateTime.Now}] STACK TRACE: {ex?.StackTrace}" + Environment.NewLine + Environment.NewLine;
            if (ex?.InnerException != null)
            {
                exceptionString += $"[{DateTime.Now}] INNER EXCEPTION: {ex.InnerException}" + Environment.NewLine + Environment.NewLine;
                exceptionString += $"[{DateTime.Now}] INNER EXCEPTION STACK TRACE: {ex.InnerException.StackTrace}" + Environment.NewLine + Environment.NewLine;
            }

            File.AppendAllText("CRASHREPORT (" + DateTime.Now.ToString("yyyy.MM.dd.HH.mm.ss") + ").log", exceptionString);
        }

        private static void CheckRequiredFiles()
        {
            Console.WriteLine("Checking required files...");

            if (!File.Exists(xmlPath))
            {
                if (File.Exists(XMLNAME)) //If the file does not exist in the built directory, check for it in the same directory
                    xmlPath = XMLNAME;
                else
                {
                    Console.WriteLine("The xml has not been built");
                    ExitAfterKeyPress();
                }
            }

            if (!File.Exists(credentialsPath))
            {
                if (File.Exists(CREDENTIALSNAME)) //If the file does not exist in the built directory, check for it in the same directory
                    credentialsPath = CREDENTIALSNAME;
                else
                {
                    Console.WriteLine("The credentials file does not exist");
                    ExitAfterKeyPress();
                }
            }

            Console.WriteLine("All required files present");
        }

        private static async Task DoBotLoop()
        {
            while (true)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                long elapsed;

                try
                {
                    redditHelper.Actions = 0; //Reset number of actions

                    Console.WriteLine("    Replying to approved posts...");
                    elapsed = stopwatch.ElapsedMilliseconds;
                    await ReplyToApprovedPosts();
                    Console.WriteLine($"    Done replying ({stopwatch.ElapsedMilliseconds - elapsed} ms)");

                    Console.WriteLine("    Checking posts for auto-reply...");
                    elapsed = stopwatch.ElapsedMilliseconds;
                    await CheckPostsForAutoReply(redditHelper.Subreddits);
                    Console.WriteLine($"    Done with auto-reply ({stopwatch.ElapsedMilliseconds - elapsed} ms)");
                }
                catch (Exception e)
                {
                    //Handle all sorts of "timeout" or internet connection errors
                    if (e is RedditHttpException ||
                        e is HttpRequestException ||
                        e is WebException ||
                        (e is TaskCanceledException && !(e as TaskCanceledException).CancellationToken.IsCancellationRequested))
                    {
                        Console.WriteLine($"    Issue connecting to reddit: {e.Message}");
                    }
                    else //If it isn't one of the errors above, it might be more serious. So throw it to be caught as an unhandled exception
                        throw;
                }

                Console.WriteLine($"Loop elapsed time: {stopwatch.ElapsedMilliseconds} ms");
                Console.WriteLine("Sleeping for 10 seconds...");
                Thread.Sleep(10000); //Sleep for 10 seconds so as not to overload reddit
            }
        }

        static List<KeyValuePair<Post, SearchResult>> postsPendingApproval = new List<KeyValuePair<Post, SearchResult>>();

        private static async Task ReplyToApprovedPosts()
        {
            postsPendingApproval.RemoveAll(p => (DateTime.UtcNow - p.Key.CreatedUTC).TotalMinutes > 15); //Remove posts that have "timed out"
            postsPendingApproval = await RemoveWhereClimbingRouteBotHasReplied(postsPendingApproval);
            if (postsPendingApproval.Count == 0)
                return;

            List<string> approvedUrls = GetApprovedPostUrls();
            List<KeyValuePair<Post, SearchResult>> approvedPosts = postsPendingApproval.Where(p => approvedUrls.Contains(p.Key.Shortlink) || p.Value.Confidence == 1).ToList();
            foreach (KeyValuePair<Post, SearchResult> post in approvedPosts)
            {
                string reply = BotReply.GetFormattedString(post.Value);
                reply += Markdown.HRule;
                reply += BotReply.GetBotLinks(post.Key);

                if (!Debugger.IsAttached)
                {
                    await post.Key.CommentAsync(reply);
                    Console.WriteLine($"\n    Auto-replied to post {post.Key.Id}");
                }

                postsPendingApproval.RemoveAll(p => p.Key == post.Key);
            }
        }

        private static async Task CheckPostsForAutoReply(List<Subreddit> subreddits)
        {
            List<Post> recentPosts = new List<Post>();
            foreach (Subreddit subreddit in subreddits)
            {
                List<Post> subredditPosts = await redditHelper.GetPosts(subreddit);
                subredditPosts.RemoveAll(p => p.IsSelfPost);
                subredditPosts.RemoveAll(p => (DateTime.UtcNow - p.CreatedUTC).TotalMinutes > 10); //Only check recent posts
                //subredditPosts.RemoveAll(p => (DateTime.UtcNow - p.CreatedUTC).TotalMinutes < 3); //Wait till posts are 3 minutes old (gives poster time to add a comment with a MP link or for the ClimbingRouteBot to respond)
                subredditPosts = RemoveBlacklisted(subredditPosts, new[] { BlacklistLevel.NoPostReplies, BlacklistLevel.OnlyKeywordReplies, BlacklistLevel.Total }); //Remove posts from users who don't want the bot to automatically reply to them
                subredditPosts = RemoveAlreadyRepliedTo(subredditPosts);
                recentPosts.AddRange(subredditPosts);
            }

            foreach (Post post in recentPosts)
            {
                try
                {
                    string postTitle = WebUtility.HtmlDecode(post.Title);

                    Console.WriteLine($"    Trying to get an automatic reply for post (/r/{post.SubredditName}): {postTitle}");

                    SearchResult searchResult = MountainProjectDataSearch.ParseRouteFromString(postTitle);
                    if (!searchResult.IsEmpty())
                    {
                        postsPendingApproval.Add(new KeyValuePair<Post, SearchResult>(post, searchResult));

                        //Until we are more confident with automatic results, we're going to request for approval for confidence values greater than 1 (less than 100%)
                        if (searchResult.Confidence > 1)
                            RequestApproval(post, searchResult);
                    }
                    else
                        Console.WriteLine("    Nothing found");

                    LogPostBeenSeen(post);
                }
                catch (RateLimitException)
                {
                    Console.WriteLine("    Rate limit hit. Postponing reply until next iteration");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"    Exception occurred with post {RedditHelper.GetFullLink(post.Permalink)}");
                    Console.WriteLine($"    {e.Message}\n{e.StackTrace}");
                }
            }
        }

        public static void RequestApproval(Post post, SearchResult searchResult)
        {
            string locationString = Regex.Replace(BotReply.GetLocationString(searchResult.FilteredResult), @"\[|\]\(.*?\)", "").Replace("Located in ", "").Replace("\n", "");
            NotifyFoundPost(WebUtility.HtmlDecode(post.Title), post.Shortlink, searchResult.FilteredResult.Name, locationString,
                        (searchResult.FilteredResult as Route).GetRouteGrade(Grade.GradeSystem.YDS).ToString(false), searchResult.FilteredResult.URL, searchResult.FilteredResult.ID);

            //FlashWindow.Flash();

            ////Ask for confirmation to post (with special console color formatting)
            //Console.ForegroundColor = ConsoleColor.Red;
            //Console.WriteLine($"\n\nI FOUND A RESULT!");

            //Console.Write("\n\tPOST: ");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //Console.WriteLine(postTitle);

            //Console.ForegroundColor = ConsoleColor.Red;
            //Console.Write("\tSUBREDDIT: ");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //Console.WriteLine($"/r/{post.SubredditName}");

            //Console.ForegroundColor = ConsoleColor.Red;
            //Console.Write("\tPOSTER: ");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //Console.WriteLine($"{post.AuthorName}");

            //Console.ForegroundColor = ConsoleColor.Red;
            //Console.Write("\t(Post is ");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //Console.Write(Math.Round((DateTime.UtcNow - post.CreatedUTC).TotalMinutes, 2).ToString());
            //Console.ForegroundColor = ConsoleColor.Red;
            //Console.WriteLine(" min old)");

            //Console.ForegroundColor = ConsoleColor.Green;
            //Console.Write("\tRESULT: ");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //Console.WriteLine(finalResult.ToString());

            //Console.ForegroundColor = ConsoleColor.Green;
            //Console.Write("\tLOCATION: ");
            //Console.ForegroundColor = ConsoleColor.Gray;
            //List<MPObject> reversedParents = finalResult.Parents.ToList();
            //reversedParents.Reverse();
            //Console.WriteLine(string.Join(" > ", reversedParents.Select(p => p.Name)));

            //Console.ForegroundColor = ConsoleColor.Red;
            //Console.Write("\nSHOULD I REPLY (y/n)? ");
            //Console.ForegroundColor = ConsoleColor.Gray;

            //string response = null;
            //try
            //{
            //    response = Reader.ReadLine(10 * 60 * 1000); //Try to get a response, but give up after 10 min (credit: https://stackoverflow.com/a/18342182/2246411)
            //}
            //catch (TimeoutException)
            //{
            //}

            //if (response == "y")
            //{
            //    string reply = BotReply.GetFormattedString(finalResult);
            //    reply += Markdown.HRule;
            //    reply += BotReply.GetBotLinks(post);

            //    await post.CommentAsync(reply);
            //    Console.WriteLine($"\n    Auto-replied to post {post.Id}");
            //}
        }

        private static Dictionary<string, BlacklistLevel> GetBlacklist()
        {
            if (!File.Exists(blacklistedPath))
                File.Create(blacklistedPath).Close();

            Dictionary<string, BlacklistLevel> result = new Dictionary<string, BlacklistLevel>();

            List<string> lines = File.ReadAllLines(blacklistedPath).ToList();
            foreach (string line in lines)
            {
                string username = line.Split(',')[0];
                BlacklistLevel blacklist = (BlacklistLevel)Convert.ToInt32(line.Split(',')[1]);
                result.Add(username, blacklist);
            }

            return result;
        }

        private static List<Post> RemoveBlacklisted(List<Post> posts, BlacklistLevel[] blacklistLevels)
        {
            Dictionary<string, BlacklistLevel> usersWithBlacklist = GetBlacklist();

            List<Post> result = posts.ToList();
            foreach (Post post in posts)
            {
                if (usersWithBlacklist.ContainsKey(post.AuthorName))
                {
                    if (blacklistLevels.Contains(usersWithBlacklist[post.AuthorName]))
                        result.Remove(post);
                }
            }

            return result;
        }

        private static async Task<List<KeyValuePair<Post, SearchResult>>> RemoveWhereClimbingRouteBotHasReplied(List<KeyValuePair<Post, SearchResult>> posts)
        {
            List<KeyValuePair<Post, SearchResult>> result = posts.ToList();
            foreach (Post post in posts.Select(p => p.Key))
            {
                List<Comment> postComments = await post.GetCommentsAsync(100);
                if (postComments.Any(c => c.AuthorName == "ClimbingRouteBot"))
                    result.RemoveAll(p => p.Key == post);
            }

            return result;
        }

        private static void LogPostBeenSeen(Post post)
        {
            if (!File.Exists(repliedToPostsPath))
                File.Create(repliedToPostsPath).Close();

            File.AppendAllLines(repliedToPostsPath, new string[] { post.Id });
        }

        private static List<Post> RemoveAlreadyRepliedTo(List<Post> posts)
        {
            if (!File.Exists(repliedToPostsPath))
                File.Create(repliedToPostsPath).Close();

            string text = File.ReadAllText(repliedToPostsPath);
            posts.RemoveAll(p => text.Contains(p.Id));

            return posts;
        }

        private static void ExitAfterKeyPress()
        {
            Console.WriteLine();
            Console.WriteLine("Press any key to exit");
            Console.Read();
            Environment.Exit(0);
        }

        public static void NotifyFoundPost(string postTitle, string postUrl, string mpResultTitle, string mpResultLoc, string mpResultGrade, string mpResultUrl, string mpResultID)
        {
            if (string.IsNullOrEmpty(requestForApprovalURL))
                return;

            List<string> parameters = new List<string>
            {
                "postTitle=" + Uri.EscapeDataString(postTitle),
                "postURL=" + Uri.EscapeDataString(postUrl),
                "mpResultTitle=" + Uri.EscapeDataString(mpResultTitle),
                "mpResultLocation=" + Uri.EscapeDataString(mpResultLoc),
                "mpResultGrade=" + Uri.EscapeDataString(mpResultGrade),
                "mpResultURL=" + Uri.EscapeDataString(mpResultUrl),
                "mpResultID=" + Uri.EscapeDataString(mpResultID)
            };

            string postData = string.Join("&", parameters);
            byte[] data = Encoding.ASCII.GetBytes(postData);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestForApprovalURL);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = data.Length;

            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }

            using (HttpWebResponse serverResponse = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(serverResponse.GetResponseStream()))
            {
                _ = reader.ReadToEnd(); //For POST requests, we don't care about what we get back
            }
        }

        public static List<string> GetApprovedPostUrls()
        {
            if (string.IsNullOrEmpty(requestForApprovalURL))
                return new List<string>();

            string response;
            HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create(requestForApprovalURL);
            httpRequest.AutomaticDecompression = DecompressionMethods.GZip;

            using (HttpWebResponse serverResponse = (HttpWebResponse)httpRequest.GetResponse())
            using (StreamReader reader = new StreamReader(serverResponse.GetResponseStream()))
            {
                response = reader.ReadToEnd();
            }

            if (response.Contains("<title>Error</title>") || string.IsNullOrEmpty(response)) //Hit an error when contacting server code
                return new List<string>();

            JObject json = JObject.Parse(response);
            return json["approvedPosts"].ToObject<List<string>>();
        }
    }

    public class Reader
    {
        private static Thread inputThread;
        private static AutoResetEvent getInput, gotInput;
        private static string input;

        static Reader()
        {
            getInput = new AutoResetEvent(false);
            gotInput = new AutoResetEvent(false);
            inputThread = new Thread(reader);
            inputThread.IsBackground = true;
            inputThread.Start();
        }

        private static void reader()
        {
            while (true)
            {
                getInput.WaitOne();
                input = Console.ReadLine();
                gotInput.Set();
            }
        }

        // omit the parameter to read a line without a timeout
        public static string ReadLine(int timeOutMillisecs = Timeout.Infinite)
        {
            getInput.Set();
            bool success = gotInput.WaitOne(timeOutMillisecs);
            if (success)
                return input;
            else
                throw new TimeoutException("User did not provide input within the timelimit.");
        }
    }

    public static class FlashWindow
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            /// <summary>
            /// The size of the structure in bytes.
            /// </summary>
            public uint cbSize;
            /// <summary>
            /// A Handle to the Window to be Flashed. The window can be either opened or minimized.
            /// </summary>
            public IntPtr hwnd;
            /// <summary>
            /// The Flash Status.
            /// </summary>
            public uint dwFlags;
            /// <summary>
            /// The number of times to Flash the window.
            /// </summary>
            public uint uCount;
            /// <summary>
            /// The rate at which the Window is to be flashed, in milliseconds. If Zero, the function uses the default cursor blink rate.
            /// </summary>
            public uint dwTimeout;
        }

        /// <summary>
        /// Stop flashing. The system restores the window to its original stae.
        /// </summary>
        public const uint FLASHW_STOP = 0;

        /// <summary>
        /// Flash the window caption.
        /// </summary>
        public const uint FLASHW_CAPTION = 1;

        /// <summary>
        /// Flash the taskbar button.
        /// </summary>
        public const uint FLASHW_TRAY = 2;

        /// <summary>
        /// Flash both the window caption and taskbar button.
        /// This is equivalent to setting the FLASHW_CAPTION | FLASHW_TRAY flags.
        /// </summary>
        public const uint FLASHW_ALL = 3;

        /// <summary>
        /// Flash continuously, until the FLASHW_STOP flag is set.
        /// </summary>
        public const uint FLASHW_TIMER = 4;

        /// <summary>
        /// Flash continuously until the window comes to the foreground.
        /// </summary>
        public const uint FLASHW_TIMERNOFG = 12;

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr GetConsoleWindow();

        /// <summary>
        /// Flash the spacified Window (Form) until it recieves focus.
        /// </summary>
        /// <returns></returns>
        public static bool Flash()
        {
            IntPtr handle = GetConsoleWindow();

            // Make sure we're running under Windows 2000 or later
            if (Win2000OrLater)
            {
                FLASHWINFO fi = Create_FLASHWINFO(handle, FLASHW_ALL | FLASHW_TIMERNOFG, uint.MaxValue, 0);
                return FlashWindowEx(ref fi);
            }
            return false;
        }

        private static FLASHWINFO Create_FLASHWINFO(IntPtr handle, uint flags, uint count, uint timeout)
        {
            FLASHWINFO fi = new FLASHWINFO();
            fi.cbSize = Convert.ToUInt32(Marshal.SizeOf(fi));
            fi.hwnd = handle;
            fi.dwFlags = flags;
            fi.uCount = count;
            fi.dwTimeout = timeout;
            return fi;
        }

        /// <summary>
        /// Flash the specified Window (form) for the specified number of times
        /// </summary>
        /// <param name="count">The number of times to Flash.</param>
        /// <returns></returns>
        public static bool Flash(uint count)
        {
            IntPtr handle = GetConsoleWindow();

            if (Win2000OrLater)
            {
                FLASHWINFO fi = Create_FLASHWINFO(handle, FLASHW_ALL, count, 0);
                return FlashWindowEx(ref fi);
            }
            return false;
        }

        /// <summary>
        /// Start Flashing the specified Window (form)
        /// </summary>
        /// <returns></returns>
        public static bool Start()
        {
            IntPtr handle = GetConsoleWindow();

            if (Win2000OrLater)
            {
                FLASHWINFO fi = Create_FLASHWINFO(handle, FLASHW_ALL, uint.MaxValue, 0);
                return FlashWindowEx(ref fi);
            }
            return false;
        }

        /// <summary>
        /// Stop Flashing the specified Window (form)
        /// </summary>
        /// <returns></returns>
        public static bool Stop()
        {
            IntPtr handle = GetConsoleWindow();

            if (Win2000OrLater)
            {
                FLASHWINFO fi = Create_FLASHWINFO(handle, FLASHW_STOP, uint.MaxValue, 0);
                return FlashWindowEx(ref fi);
            }
            return false;
        }

        /// <summary>
        /// A boolean value indicating whether the application is running on Windows 2000 or later.
        /// </summary>
        private static bool Win2000OrLater
        {
            get { return System.Environment.OSVersion.Version.Major >= 5; }
        }
    }
}
