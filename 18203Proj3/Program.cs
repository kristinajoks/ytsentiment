using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using System.Reactive.Concurrency;

//sentiment analysis
using Microsoft.ML;
using Spectre.Console;
using Console = Spectre.Console.AnsiConsole;
using _18203Proj3;
using System.Text.RegularExpressions;

public class VideoComment
{
    public string VideoId { get; set; }
    public string VideoTitle { get; set; }
    public string Comment { get; set; }

    public int positiveCount = 0;
    public int negativeCount = 0;
}

public class VideoCommentStream : IObservable<VideoComment>
{
    private readonly Subject<VideoComment> commentSubject;
    private readonly TaskCompletionSource<bool> processingCompleted;


    public VideoCommentStream()
    {
        commentSubject = new Subject<VideoComment>();
        processingCompleted = new TaskCompletionSource<bool>();
    }

    public void GetVideoCommentsConcurrent(IEnumerable<string> videoIds, YouTubeService youtubeService)
    {
        try
        {
            var videoIdsObservable = videoIds.ToObservable();

            var scheduler = ThreadPoolScheduler.Instance;

            //CountdownEvent to track the completion of all comment threads
            var countdownEvent = new CountdownEvent(videoIds.Count());

            foreach (var videoId in videoIds)
            {
                var videoRequest = youtubeService.Videos.List("snippet,statistics");
                videoRequest.Id = videoId;

                var commentThreadsObservable = Observable.FromAsync(() => videoRequest.ExecuteAsync())
                    .SelectMany(videoResponse => videoResponse.Items)
                    .Select(video => new
                    {
                        Video = video,
                        VideoId = video.Id,
                        VideoTitle = video.Snippet?.Title,
                        CommentCount = video.Statistics?.CommentCount ?? 0
                    })
                    .Do(videoInfo =>
                    {
                        if (videoInfo.VideoTitle == null)
                        {
                            Console.WriteLine($"Information about the video with ID: {videoInfo.VideoId} is not available.");
                        }
                        else if (videoInfo.CommentCount == 0)
                        {
                            Console.WriteLine("Video with ID: {videoInfo.VideoId}, titled: {videoInfo.VideoTitle}, has NO comments or they are DISABLED.\n");
                        }
                    })
                    .Where(videoInfo => videoInfo.VideoTitle != null && videoInfo.CommentCount > 0)
                    .SelectMany(videoInfo =>
                    {
                        var commentThreadsRequest = youtubeService.CommentThreads.List("snippet");
                        commentThreadsRequest.VideoId = videoInfo.VideoId;
                        commentThreadsRequest.Order = CommentThreadsResource.ListRequest.OrderEnum.Relevance;

                        return Observable.FromAsync(() => commentThreadsRequest.ExecuteAsync())
                            .SelectMany(commentThreadsResponse => commentThreadsResponse.Items)
                            .Select(commentThread => new VideoComment
                            {
                                VideoId = videoInfo.VideoId,
                                Comment = commentThread.Snippet.TopLevelComment.Snippet.TextOriginal,
                                VideoTitle = videoInfo.VideoTitle
                            })
                            .ObserveOn(scheduler); //Set the scheduler on which operations within the Observable sequence will execute
                    });

                commentThreadsObservable
                    .SubscribeOn(scheduler) //Set the scheduler on which the subscription to the Observable sequence will occur
                    .Subscribe(
                        videoComment =>
                        {
                            commentSubject.OnNext(videoComment);
                        },
                        error =>
                        {
                            commentSubject.OnError(error);
                            processingCompleted.SetException(error);
                        },
                        () => 
                        {
                            Console.WriteLine($"Thread {Thread.CurrentThread.ManagedThreadId} has completed processing comments");
                            //Signal that a comment thread has completed

                            countdownEvent.Signal();
                        }
                    );
            }

            scheduler.Schedule(() =>
            {
                countdownEvent.Wait(); //Wait for all comment threads to complete
                commentSubject.OnCompleted();
                processingCompleted.SetResult(true); //Set the result to true when processing is completed
            });
        }
        catch (Exception e)
        {
            commentSubject.OnError(e);
        }
    }


    public Task<bool> WaitForProcessingCompletion()
    {
        return processingCompleted.Task;
    }

    public IDisposable Subscribe(IObserver<VideoComment> observer)
    {
        return commentSubject.Subscribe(observer);
    }
}

public class VideoCommentObserver : IObserver<VideoComment>
{
    private readonly string name;
    //
    private Dictionary<string, int> positiveCounts;
    private Dictionary<string, int> negativeCounts;

    public VideoCommentObserver(string name)
    {
        this.name = name;
        positiveCounts = new Dictionary<string, int>();
        negativeCounts = new Dictionary<string, int>();
    }

    public void OnNext(VideoComment comment)
    {
        Program.Analyse(comment);

        if (comment.positiveCount > 0)
        {
            if (positiveCounts.ContainsKey(comment.VideoId))
                positiveCounts[comment.VideoId] += comment.positiveCount;
            else
                positiveCounts[comment.VideoId] = comment.positiveCount;
        }

        if (comment.negativeCount > 0)
        {
            if (negativeCounts.ContainsKey(comment.VideoId))
                negativeCounts[comment.VideoId] += comment.negativeCount;
            else
                negativeCounts[comment.VideoId] = comment.negativeCount;
        }
    }

    public void OnError(Exception error)
    {
        Console.WriteLine($"{name}: An error occurred: {error.Message}");
    }

    public void OnCompleted()
    {
        Console.WriteLine($"\n{name}: All comments retrieved.");

        foreach (var videoId in positiveCounts.Keys)
        {
            int positiveCount = positiveCounts[videoId];
            int negativeCount = negativeCounts.ContainsKey(videoId) ? negativeCounts[videoId] : 0;
            double totalComments = positiveCount + negativeCount;
            double positivePercentage = (positiveCount / totalComments) * 100;
            double negativePercentage = (negativeCount / totalComments) * 100;

            Console.WriteLine($"Video ID: {videoId}");
            Console.WriteLine($"Positive comments: {positivePercentage:F2}%");
            Console.WriteLine($"Negative comments: {negativePercentage:F2}%");
            Console.WriteLine("\n");
        }

        Console.WriteLine("-------------------------------------");
    }
}

public class Program
{
    public static void Analyse(VideoComment comment)
    {
        try
        {
            string text = comment.Comment;
            var engine = ctx.Model.CreatePredictionEngine<SentimentData, SentimentPrediction>(model);

            var input = new SentimentData { SentimentText = text };
            var result = engine.Predict(input);
            var style = result.Prediction
               ? (color: "green", text: "positive")
               : (color: "red", text: "negative");

            //Removing special characters and emoticons
            char[] specialChars = { '[', ']', '{', '}', '"' }; //':',

            foreach (char specialChar in specialChars)
            {
                if (text.Contains(specialChar))
                {
                    text = text.Replace(specialChar.ToString(), "\\" + specialChar);
                }
            }

            if (Regex.IsMatch(text, @"\p{Cs}"))
            {
                text = Regex.Replace(text, @"\p{Cs}", "");
            }
            //

            if (result.Prediction)
            {
                comment.positiveCount++;
            }
            else
            {
                comment.negativeCount++;
            }

            Console.MarkupLine($"Thread {Thread.CurrentThread.ManagedThreadId} processed comment for Video ID: {comment.VideoId}, Title: {comment.VideoTitle}, \n " +
                $"{style.text} [{style.color}]\"{text}\" ({result.Probability:P00})[/] " +
                "\n ------------------------------------------------------------ \n");
        }
        catch(Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }
    }

    public static MLContext ctx = new MLContext();
    public static ITransformer model = default!;

    static async Task Main(string[] args)
    {
        var dataView = ctx.Data.LoadFromTextFile<SentimentData>("Data\\yelp_labelled.txt");

        //Splitting data into testing set
        var splitDataView = ctx.Data.TrainTestSplit(dataView, testFraction: 0.2);

        //Building model
        var estimator = ctx.Transforms.Text
            .FeaturizeText(
                outputColumnName: "Features",
                inputColumnName: nameof(SentimentData.SentimentText)
            ).Append(ctx.BinaryClassification.Trainers.SdcaLogisticRegression(featureColumnName: "Features"));

        var rule = new Rule("Create and Train Model");
        Console
            .Live(rule)
            .Start(console =>
            {
                //Training happens here
                model = estimator.Fit(splitDataView.TrainSet);
                var predictions = model.Transform(splitDataView.TestSet);

                rule.Title = "Training Complete, Evaluating Accuracy.";
                console.Refresh();

                //Evaluating the accuracy, Area Under the ROC Curve and F1 score of our model
                var metrics = ctx.BinaryClassification.Evaluate(predictions);

                var table = new Table()
                    .MinimalBorder()
                    .Title("Model Accuracy");
                table.AddColumns("Accuracy", "Auc", "F1Score");
                table.AddRow($"{metrics.Accuracy:P2}", $"{metrics.AreaUnderRocCurve:P2}", $"{metrics.F1Score:P2}");

                console.UpdateTarget(table);
                console.Refresh();
            });


        //Creating YT API client
        var youtubeService = new YouTubeService(new BaseClientService.Initializer()
        {
            ApiKey = "AIzaSyAMgE1Exc-l3_6Vy0ts5fTxRxDS6uxPGiI"
        });

        var videoIds = new List<string>();
        //{
        //    "pBk4NYhWNMM", //barbie trailer
        //    "OM4zUZuZl90", //arthur 2h comments disabled
        //    "fF25lh_Ib4c" //bird doc serbian
        //};

        Console.WriteLine("Add video IDs. Type \"End\" when done.");
        var text = "";
        while (text != "End")
        {
            text = AnsiConsole.Ask<string>("What's your video ID?");
            if(!videoIds.Contains(text))
                videoIds.Add(text);
        }


        //Creating video comment stream
        var videoCommentStream = new VideoCommentStream();

        //Comments count can be limited this way (subscribe to this stream instead)
        //var limitedVideoCommentStream = videoCommentStream.Take(15); 

        //Creating Observers
        var observer1 = new VideoCommentObserver("Observer 1");
        var observer2 = new VideoCommentObserver("Observer 2");
        var observer3 = new VideoCommentObserver("Observer 3");

        //Subscriptions
        var subscription1 = videoCommentStream.Subscribe(observer1);
        var subscription2 = videoCommentStream.Subscribe(observer2);
        var subscription3 = videoCommentStream.Subscribe(observer3);

        //Retrieval of data
        videoCommentStream.GetVideoCommentsConcurrent(videoIds, youtubeService);

        await videoCommentStream.WaitForProcessingCompletion(); //Necessary wait 

        //Disposing
        subscription1.Dispose();
        subscription2.Dispose();
        subscription3.Dispose();
    }
}

