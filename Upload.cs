using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Data.SqlClient;


using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

using FFX.Shared.Logic.Mongo;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;

using Quartz;
using Quartz.Impl;

namespace VideoToYoutube
{
    class Program
    {
        static void Main()
        {
            // scheduler used as Youtube only allows 99 videos per day per account
            ISchedulerFactory schedulerFactory = new StdSchedulerFactory();
            IScheduler Scheduler = schedulerFactory.GetScheduler();

            IJobDetail Job = JobBuilder.Create<HelloJob>()
                .Build();

            ITrigger Trigger = TriggerBuilder.Create()
                .WithCronSchedule("00 00 08 * * ?")
                .StartNow()
                .Build();

            Scheduler.ScheduleJob(Job, Trigger);
            Scheduler.Start();
        }
    }

    public class HelloJob : IJob
    {
        public void Execute(IJobExecutionContext context)
        {
            var a = new Start(new InfoGather(@"C:\Users\Arjun.Prasad\Downloads\Videos\360video").getVidDetails());
            new PlaylistUpdates().start();
            Console.WriteLine("done");
            Console.WriteLine();
            Console.WriteLine("Not done : ");
            foreach (var aa in a.notUp1)
            {
                Console.Write(aa);
            }
        }
    }

    internal class Start
    {
        private bool error = false;
        public List<string> notUp1 = new List<string>();

        public Start(List<VideoDetails> VD)
        {
            int i = 0;

            foreach(var Video in VD)
            {
                Upload(Video);

                if (error)
                {
                    break;
                }

                i++;

                if (i >= 97)
                {
                    Console.WriteLine();
                    Console.WriteLine("-------------------- 97 videos uploaded ----------------------");
                    Console.WriteLine();
                    break;
                }
            }
        }

        private void Upload(VideoDetails Video)
        {
            int SKU = Video.SKU;
            string ManPartNum = Video.PartNum;
            string Title = Video.WebTitle;
            Title = Title.Length > 100 ? Title.Substring(0,100) : Title;
            int len = Title.Length;
            string Desc = Video.vidDesc;
            string FilePath = $@"C:\Users\Arjun.Prasad\Downloads\Videos\360video\{ManPartNum}\index.files\html5video\{ManPartNum}.m4v";
            string tempTags = Video.Keywords;
            string[] Tags = String.IsNullOrEmpty(tempTags) ? null : Video.Keywords.Split(',');

            UploadVideo UV = new UploadVideo(Title, Desc, Tags, FilePath, SKU);

            if (UV.uploadUpdate())
            {
                if(UV.notUp != null)
                {
                    notUp1.Add(UV.notUp);
                }
                else
                {
                    MongoUpdate MU = new MongoUpdate(SKU, UV.getvideoID(), ManPartNum);
                }
            }
            else
            {
                error = true;
            }
        }
    }

    internal class MongoUpdate
    {
        private IMongoCollection<BsonDocument> Videos = MongoDbHelper.GetCollection("db", "contacts");

        // update video details onto the database
        public MongoUpdate(int SKU, string VideoID, string PartNum)
        {
            try
            {
                if (check(SKU))
                    add(SKU, VideoID);
                else
                    update(SKU, VideoID);
                FolderDel(PartNum);
            }
            catch(Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        // delete folder its been emptied
        private void FolderDel(string PartNum)
        {
            string path = $@"C:\Users\Arjun.Prasad\Downloads\Videos\360video\{PartNum}";
            DirectoryInfo attachments_AR = new DirectoryInfo(path);
            EmptyFolder(attachments_AR);
            Directory.Delete(path);
        }

        // empty folder once the video has been uploaded
        private void EmptyFolder(DirectoryInfo directory)
        {
            foreach (FileInfo file in directory.GetFiles())
            {
                file.Delete();
            }
            foreach (DirectoryInfo subdirectory in directory.GetDirectories())
            {
                EmptyFolder(subdirectory);
                subdirectory.Delete();
            }
        }
        
        public bool check(int SKU)
        {
            var filter = Builders<BsonDocument>.Filter.Eq<int>("sku", SKU);
            var res = Videos.Find(filter).Count();
            if (res == 0)
                return true;
            return false;
        }

        // add the newly uploaded video details onto database
        void add(int SKU, string videoID)
        {
            Product_Videos Video = new Product_Videos();
            Video.sku = SKU;
            Video.Thumbnail = $"https://img.youtube.com/vi/{videoID}/1.jpg";
            Video.VideoObjectString = $"<object type=\"text/html\" allowscriptaccess=\"always\" allowfullscreen=\"true\" data=\"//www.youtube-nocookie.com/embed/{videoID}/rel=0\" height=\"360\" width=\"640\"></object>";
            Videos.InsertOne(BsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(Video)));
        }

        // if the video details already exists, update the details
        void update(int SKU, string videoID)
        {
            var filter = Builders<BsonDocument>.Filter.Eq<int>("sku", SKU);
            var update = Builders<BsonDocument>.Update
                .Set("Thumbnail", $"https://img.youtube.com/vi/{videoID}/1.jpg")
                .Set("VideoObjectString", $"<object type=\"text/html\" allowscriptaccess=\"always\" allowfullscreen=\"true\" data=\"//www.youtube-nocookie.com/embed/{videoID}/rel=0\" height=\"360\" width=\"640\"></object>");
            Videos.UpdateOne(filter, update);
        }
    }

    internal class UploadVideo
    {
        public string notUp = null;
        private bool upload = false;
        private string videoID;

        //[STAThread]
        public UploadVideo(string Title, string Desc, string[] Tags, string FilePath, int SKU)
        {
            Console.WriteLine("YouTube Data API: Upload Video");
            Console.WriteLine("==============================");

            try
            {
                Run(Title, Desc, Tags, FilePath).Wait();
            }
            catch (AggregateException ex)
            {
                foreach (var e in ex.InnerExceptions)
                {
                    if(e.Message.ToLower().StartsWith("could not find"))
                    {
                        upload = true;
                        notUp = e.Message.Split('\\').Last();
                    }
                    Console.WriteLine("Error: " + e.Message);
                }
            }

            Console.WriteLine();
        }

        private async Task Run(string title, string desc, string[] tags, string filepath)
        {
            UserCredential credential;
            using (var stream = new FileStream("client_secrets.json", FileMode.Open, FileAccess.Read))
            {
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    // This OAuth 2.0 access scope allows an application to upload files to the
                    // authenticated user's YouTube channel, but doesn't allow other types of access.
                    new[] { YouTubeService.Scope.YoutubeUpload },
                    "user",
                    CancellationToken.None
                );
            }

            var youtubeService = new YouTubeService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = Assembly.GetExecutingAssembly().GetName().Name
            });

            // list all details into the object
            var video = new Video();
            video.Snippet = new VideoSnippet();
            video.Snippet.Title = title;
            video.Snippet.Description = desc;
            video.Snippet.Tags = tags;
            video.Snippet.CategoryId = "28"; // See https://developers.google.com/youtube/v3/docs/videoCategories/list
            video.Status = new VideoStatus();
            video.Status.PrivacyStatus = "unlisted"; // or "private" or "public"
            var filePath = filepath; // Replace with path to actual movie file.

            using (var fileStream = new FileStream(filePath, FileMode.Open))
            {
                var videosInsertRequest = youtubeService.Videos.Insert(video, "snippet,status", fileStream, "video/*");
                videosInsertRequest.ProgressChanged += videosInsertRequest_ProgressChanged;
                videosInsertRequest.ResponseReceived += videosInsertRequest_ResponseReceived;

                await videosInsertRequest.UploadAsync();
            }
        }

        void videosInsertRequest_ProgressChanged(IUploadProgress progress)
        {
            switch (progress.Status)
            {
                case UploadStatus.Uploading:
                    Console.WriteLine("{0} bytes sent.", progress.BytesSent);
                    break;

                case UploadStatus.Failed:
                    Console.WriteLine("An error prevented the upload from completing.\n{0}", progress.Exception);
                    break;
            }
        }

        void videosInsertRequest_ResponseReceived(Video video)
        {
            upload = true;
            videoID = video.Id;
            Console.WriteLine("Video id '{0}' was successfully uploaded.", videoID);
        }

        public string getvideoID()
        {
            return videoID;
        }

        public bool uploadUpdate()
        {
            return upload;
        }
    }

    internal class InfoGather
    {
        private List<VideoDetails> VidDetails = new List<VideoDetails>();

        // get video details
        public InfoGather(string path)
        {
            setVidDetails(setPartNum(path));
        }

        private List<string> setPartNum(string path)
        {
            List<string> ManPartNum = new List<string>();
            string[] Paths = Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly);
            foreach (var Path in Paths)
            {
                ManPartNum.Add(Path.Split('\\')[6]);
            }
            return ManPartNum;
        }

        private void setVidDetails(List<string> ManPartNum)
        {
            string connectionString = ConfigurationManager.AppSettings["SQLConn"];
            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                string temp = "PartNum = '" + ManPartNum[0]+ "'";

                foreach(var num in ManPartNum)
                {
                    temp += " OR PartNum = '" + num + "'";
                }

                SqlCommand command = new SqlCommand("SELECT * FROM goods fs " +
                        "inner join supplier ps on ps.ProductID = fs.ID " +
                        $"WHERE ps.SupplierID = 111 and fs.Status in (0, 3) and ({temp})", conn);

                conn.Open();

                var read = command.ExecuteReader();

                while (read.Read())
                {
                    VideoDetails VD = new VideoDetails()
                    {
                        PartNum = read["Num"].ToString().Trim(),
                        WebTitle = read["Title"].ToString().Trim(),
                        SKU = (int)read["ID"],
                        Keywords = read["Key"].ToString().Trim(),
                        vidDesc = read["Desc"].ToString().Trim(),
                    };

                    VidDetails.Add(VD);
                }
            }
        }

        public List<VideoDetails> getVidDetails()
        {
            return VidDetails;
        }
    }

    internal class PlaylistUpdates
    {
        [STAThread]
        public void start()
        {

            Console.WriteLine("YouTube Data API: Playlist Updates");
            Console.WriteLine("==================================");

            try
            {
                new PlaylistUpdates().Run().Wait();
            }
            catch (AggregateException ex)
            {
                foreach (var e in ex.InnerExceptions)
                {
                    Console.WriteLine("Error: " + e.Message);
                }
            }
        }

        private async Task Run()
        {

            UserCredential credential;
            using (var stream = new FileStream("client_secrets.json", FileMode.Open, FileAccess.Read))
            {
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    // This OAuth 2.0 access scope allows for read-only access to the authenticated 
                    // user's account, but not other types of account access.
                    new[] { YouTubeService.Scope.Youtube },
                    "user",
                    CancellationToken.None,
                    new FileDataStore(this.GetType().ToString())
                );
            }

            var youtubeService = new YouTubeService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = this.GetType().ToString()
            });

            List<PlayList> PLDetail = new List<PlayList>();
            List<Videos> Videos = new List<Videos>();

            var channelsListRequest = youtubeService.Channels.List("contentDetails");
            channelsListRequest.Mine = true;

            var nextPageToken = "";
            while (nextPageToken != null)
            {
                var playlists = youtubeService.Playlists.List("snippet");
                playlists.PageToken = nextPageToken;
                playlists.MaxResults = 50;
                playlists.Mine = true;
                PlaylistListResponse presponse = await playlists.ExecuteAsync();
                int i = 0;
                foreach (var currentPlayList in presponse.Items)
                {
                    string temp = currentPlayList.Snippet.Title;
                    Console.WriteLine(i + " : " + temp);

                    PLDetail.Add(new PlayList()
                    {
                        name = temp.Trim().ToLower(),
                        ID = currentPlayList.Id
                    });

                    i++;
                }
                nextPageToken = presponse.NextPageToken;
            }

            // Retrieve the contentDetails part of the channel resource for the authenticated user's channel.
            var channelsListResponse = await channelsListRequest.ExecuteAsync();

            // get todays date
            string Today = DateTime.Today.ToString().Substring(0, 10);

            foreach (var channel in channelsListResponse.Items)
            {
                // From the API response, extract the playlist ID that identifies the list
                // of videos uploaded to the authenticated user's channel.
                var uploadsListId = channel.ContentDetails.RelatedPlaylists.Uploads;

                Console.WriteLine("Videos in list {0}", uploadsListId);

                nextPageToken = "";
                while (nextPageToken != null)
                {
                    var playlistItemsListRequest = youtubeService.PlaylistItems.List("snippet");
                    playlistItemsListRequest.PlaylistId = uploadsListId;
                    playlistItemsListRequest.MaxResults = 50;
                    playlistItemsListRequest.PageToken = nextPageToken;

                    // Retrieve the list of videos uploaded to the authenticated user's channel.
                    var playlistItemsListResponse = await playlistItemsListRequest.ExecuteAsync();

                    foreach (var playlistItem in playlistItemsListResponse.Items)
                    {
                        string ItemDate = playlistItem.Snippet.PublishedAt.ToString();
                        if (ItemDate.StartsWith(Today))
                        {
                            Videos.Add(new Videos()
                            {
                                name = playlistItem.Snippet.Title.Trim().ToLower(),
                                ID = playlistItem.Snippet.ResourceId.VideoId
                            });
                        }
                    }

                    nextPageToken = playlistItemsListResponse.NextPageToken;
                }
            }

            foreach (var Video in Videos)
            {
                foreach (var PL in PLDetail)
                {
                    if (Video.name.Contains(PL.name))
                    {
                        // Add video to the matching playlist.
                        var newPlaylistItem = new PlaylistItem();
                        newPlaylistItem.Snippet = new PlaylistItemSnippet();
                        newPlaylistItem.Snippet.PlaylistId = PL.ID;
                        newPlaylistItem.Snippet.ResourceId = new ResourceId();
                        newPlaylistItem.Snippet.ResourceId.Kind = "youtube#video";
                        newPlaylistItem.Snippet.ResourceId.VideoId = Video.ID;
                        newPlaylistItem = await youtubeService.PlaylistItems.Insert(newPlaylistItem, "snippet").ExecuteAsync();

                        break;
                    }
                }
            }
        }
    }
}
