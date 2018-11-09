﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Cognitive Services: http://www.microsoft.com/cognitive
// 
// Microsoft Cognitive Services Github:
// https://github.com/Microsoft/Cognitive
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// 

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.ProjectOxford.Face.Contract;
using Newtonsoft.Json;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using VideoFrameAnalyzer;
using Common = Microsoft.ProjectOxford.Common;
using FaceAPI = Microsoft.ProjectOxford.Face;
using VisionAPI = Microsoft.ProjectOxford.Vision;
using Tsunami.SafetyLine.API.Client;
using Tsunami.SafetyLine.API.Client.Models;
using Tsunami.Coaster.ApplicationService.Models;
using System.Configuration;
using Tsunami.Coaster.ApplicationService;
using System.Web;

namespace LiveCameraSample
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        private FaceAPI.FaceServiceClient _faceClient = null;
        private VisionAPI.VisionServiceClient _visionClient = null;
        private readonly FrameGrabber<LiveCameraResult> _grabber = null;
        private static readonly ImageEncodingParam[] s_jpegParams = {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 60)
        };
        private readonly CascadeClassifier _localFaceDetector = new CascadeClassifier();
        private bool _fuseClientRemoteResults;
        private LiveCameraResult _latestResultsToDisplay = null;
        private AppMode _mode;
        private DateTime _startTime;
        private const string _personGroupId = "myself_test";

        private readonly IUserAppService _userAppService;
        private readonly ICoasterPhoneCall _coasterPhoneCall;

        public enum AppMode
        {
            Faces,
            Emotions,
            EmotionsWithClientFaceDetect,
            Tags,
            Celebrities,
            Comparison
        }

        public MainWindow()
        {
            InitializeComponent();

            // Create grabber. 
            _grabber = new FrameGrabber<LiveCameraResult>();

            // Set up a listener for when the client receives a new frame.
            _grabber.NewFrameProvided += (s, e) =>
            {
                if (_mode == AppMode.EmotionsWithClientFaceDetect)
                {
                    // Local face detection. 
                    var rects = _localFaceDetector.DetectMultiScale(e.Frame.Image);
                    // Attach faces to frame. 
                    e.Frame.UserData = rects;
                }

                // The callback may occur on a different thread, so we must use the
                // MainWindow.Dispatcher when manipulating the UI. 
                this.Dispatcher.BeginInvoke((Action)(() =>
                {
                    // Display the image in the left pane.
                    LeftImage.Source = e.Frame.Image.ToBitmapSource();

                    // If we're fusing client-side face detection with remote analysis, show the
                    // new frame now with the most recent analysis available. 
                    if (_fuseClientRemoteResults)
                    {
                        RightImage.Source = VisualizeResult(e.Frame);
                    }
                }));

                // See if auto-stop should be triggered. 
                if (Properties.Settings.Default.AutoStopEnabled && (DateTime.Now - _startTime) > Properties.Settings.Default.AutoStopTime)
                {
                    _grabber.StopProcessingAsync();
                }
            };

            // Set up a listener for when the client receives a new result from an API call. 
            _grabber.NewResultAvailable += (s, e) =>
            {
                this.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (e.TimedOut)
                    {
                        MessageArea.Text = "API call timed out.";
                    }
                    else if (e.Exception != null)
                    {
                        string apiName = "";
                        string message = e.Exception.Message;
                        var faceEx = e.Exception as FaceAPI.FaceAPIException;
                        var emotionEx = e.Exception as Common.ClientException;
                        var visionEx = e.Exception as VisionAPI.ClientException;
                        if (faceEx != null)
                        {
                            apiName = "Face";
                            message = faceEx.ErrorMessage;
                        }
                        else if (emotionEx != null)
                        {
                            apiName = "Emotion";
                            message = emotionEx.Error.Message;
                        }
                        else if (visionEx != null)
                        {
                            apiName = "Computer Vision";
                            message = visionEx.Error.Message;
                        }
                        MessageArea.Text = string.Format("{0} API call failed on frame {1}. Exception: {2}", apiName, e.Frame.Metadata.Index, message);
                    }
                    else
                    {
                        _latestResultsToDisplay = e.Analysis;

                        // Display the image and visualization in the right pane. 
                        if (!_fuseClientRemoteResults)
                        {
                            RightImage.Source = VisualizeResult(e.Frame);
                        }
                    }
                }));
            };

            // Create local face detector. 
            _localFaceDetector.Load("Data/haarcascade_frontalface_alt2.xml");

            //TODO: Coaster
            _userAppService = new UserAppService();
            _coasterPhoneCall = new CoasterPhoneCall();
        }

        /// <summary> Function which submits a frame to the Face API. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the faces returned by the API. </returns>
        private async Task<LiveCameraResult> FacesAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var attrs = new List<FaceAPI.FaceAttributeType> {
                FaceAPI.FaceAttributeType.Age,
                FaceAPI.FaceAttributeType.Gender,
                FaceAPI.FaceAttributeType.HeadPose,
            };
            var faces = await _faceClient.DetectAsync(jpg, returnFaceAttributes: attrs);
            // Count the API call. 
            Properties.Settings.Default.FaceAPICallCount++;
            // Output. 
            return new LiveCameraResult { Faces = faces };
        }

        /// <summary> Function which submits a frame to the Emotion API. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the emotions returned by the API. </returns>
        private async Task<LiveCameraResult> EmotionAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            FaceAPI.Contract.Face[] faces = null;

            // See if we have local face detections for this image.
            var localFaces = (OpenCvSharp.Rect[])frame.UserData;
            if (localFaces == null || localFaces.Count() > 0)
            {
                // If localFaces is null, we're not performing local face detection.
                // Use Cognigitve Services to do the face detection.
                Properties.Settings.Default.FaceAPICallCount++;
                faces = await _faceClient.DetectAsync(
                    jpg,
                    /* returnFaceId= */ false,
                    /* returnFaceLandmarks= */ false,
                    new FaceAPI.FaceAttributeType[1] { FaceAPI.FaceAttributeType.Emotion });
            }
            else
            {
                // Local face detection found no faces; don't call Cognitive Services.
                faces = new FaceAPI.Contract.Face[0];
            }

            var emotions = faces.Select(e => e.FaceAttributes.Emotion).ToArray();
            foreach (var emotion in emotions)
            {
                if (emotion.Sadness >= 0.6)
                {

                    MessageBox.Show($"I've detected you are {emotion.Sadness.ToString()} sad, something to cheer you up is on it's way!");
                    await AddPanicEmergency(emotion.Sadness.ToString());

                    

                }
            }

            // Output. 
            return new LiveCameraResult
            {
                Faces = faces.Select(e => CreateFace(e.FaceRectangle)).ToArray(),
                // Extract emotion scores from results. 
                EmotionScores = faces.Select(e => e.FaceAttributes.Emotion).ToArray()
                
                
            };
        }

        /// <summary> Function which submits a frame to the Computer Vision API for tagging. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the tags returned by the API. </returns>
        private async Task<LiveCameraResult> TaggingAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var analysis = await _visionClient.GetTagsAsync(jpg);
            // Count the API call. 
            Properties.Settings.Default.VisionAPICallCount++;
            // Output. 
            return new LiveCameraResult { Tags = analysis.Tags };
        }

        /// <summary> Function which submits a frame to the Computer Vision API for celebrity
        ///     detection. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the celebrities returned by the API. </returns>
        private async Task<LiveCameraResult> CelebrityAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var result = await _visionClient.AnalyzeImageInDomainAsync(jpg, "celebrities");
            // Count the API call. 
            Properties.Settings.Default.VisionAPICallCount++;
            // Output. 
            var celebs = JsonConvert.DeserializeObject<CelebritiesResult>(result.Result.ToString()).Celebrities;
            return new LiveCameraResult
            {
                // Extract face rectangles from results. 
                Faces = celebs.Select(c => CreateFace(c.FaceRectangle)).ToArray(),
                // Extract celebrity names from results. 
                CelebrityNames = celebs.Select(c => c.Name).ToArray()
            };
        }

        private async Task<LiveCameraResult> ComparisonFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);

            var faceDetect = await _faceClient.DetectAsync(jpg, true);
            var faceIds = faceDetect.Select(face => face.FaceId).ToArray();

            var results = await _faceClient.IdentifyAsync(_personGroupId, faceIds);

            foreach (var identifyResult in results)
            {
                if (identifyResult.Candidates.Length == 0)
                {
                    //var testFaces = await _faceClient.DetectAsync(jpg);
                    //return new LiveCameraResult { Faces = testFaces };
                }
                else
                {
                    var candidateId = identifyResult.Candidates[0].PersonId;
                    var person = await _faceClient.GetPersonAsync(_personGroupId, candidateId);
                    Console.WriteLine($"Identified as {person.Name}");
                    return new LiveCameraResult
                    {
                        Faces = faceDetect,
                        PersonName = person.Name
                    };
                }
            }

            // Count the API call. 
            Properties.Settings.Default.FaceAPICallCount++;
            // Output. 
            return new LiveCameraResult { Faces = faceDetect };
        }

        private BitmapSource VisualizeResult(VideoFrame frame)
        {
            // Draw any results on top of the image. 
            BitmapSource visImage = frame.Image.ToBitmapSource();

            var result = _latestResultsToDisplay;

            if (result != null)
            {
                // See if we have local face detections for this image.
                var clientFaces = (OpenCvSharp.Rect[])frame.UserData;
                if (clientFaces != null && result.Faces != null)
                {
                    // If so, then the analysis results might be from an older frame. We need to match
                    // the client-side face detections (computed on this frame) with the analysis
                    // results (computed on the older frame) that we want to display. 
                    MatchAndReplaceFaceRectangles(result.Faces, clientFaces);
                }

                visImage = Visualization.DrawFaces(visImage, result.Faces, result.EmotionScores, result.CelebrityNames, result.PersonName);
                visImage = Visualization.DrawTags(visImage, result.Tags);
            }

            return visImage;
        }

        /// <summary> Populate CameraList in the UI, once it is loaded. </summary>
        /// <param name="sender"> Source of the event. </param>
        /// <param name="e">      Routed event information. </param>
        private void CameraList_Loaded(object sender, RoutedEventArgs e)
        {
            int numCameras = _grabber.GetNumCameras();

            if (numCameras == 0)
            {
                MessageArea.Text = "No cameras found!";
            }

            var comboBox = sender as ComboBox;
            comboBox.ItemsSource = Enumerable.Range(0, numCameras).Select(i => string.Format("Camera {0}", i + 1));
            comboBox.SelectedIndex = 0;
        }

        /// <summary> Populate ModeList in the UI, once it is loaded. </summary>
        /// <param name="sender"> Source of the event. </param>
        /// <param name="e">      Routed event information. </param>
        private void ModeList_Loaded(object sender, RoutedEventArgs e)
        {
            var modes = (AppMode[])Enum.GetValues(typeof(AppMode));

            var comboBox = sender as ComboBox;
            comboBox.ItemsSource = modes.Select(m => m.ToString());
            comboBox.SelectedIndex = 0;
        }

        private void ModeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Disable "most-recent" results display. 
            _fuseClientRemoteResults = false;

            var comboBox = sender as ComboBox;
            var modes = (AppMode[])Enum.GetValues(typeof(AppMode));
            _mode = modes[comboBox.SelectedIndex];
            switch (_mode)
            {
                case AppMode.Faces:
                    _grabber.AnalysisFunction = FacesAnalysisFunction;
                    break;
                case AppMode.Emotions:
                    _grabber.AnalysisFunction = EmotionAnalysisFunction;
                    break;
                case AppMode.EmotionsWithClientFaceDetect:
                    // Same as Emotions, except we will display the most recent faces combined with
                    // the most recent API results. 
                    _grabber.AnalysisFunction = EmotionAnalysisFunction;
                    _fuseClientRemoteResults = true;
                    break;
                case AppMode.Tags:
                    _grabber.AnalysisFunction = TaggingAnalysisFunction;
                    break;
                case AppMode.Celebrities:
                    _grabber.AnalysisFunction = CelebrityAnalysisFunction;
                    break;
                case AppMode.Comparison:
                    _grabber.AnalysisFunction = ComparisonFunction;
                    break;
                default:
                    _grabber.AnalysisFunction = null;
                    break;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CameraList.HasItems)
            {
                MessageArea.Text = "No cameras found; cannot start processing";
                return;
            }

            // Clean leading/trailing spaces in API keys. 
            Properties.Settings.Default.FaceAPIKey = Properties.Settings.Default.FaceAPIKey.Trim();
            Properties.Settings.Default.VisionAPIKey = Properties.Settings.Default.VisionAPIKey.Trim();

            // Create API clients. 
            _faceClient = new FaceAPI.FaceServiceClient(Properties.Settings.Default.FaceAPIKey, Properties.Settings.Default.FaceAPIHost);
            _visionClient = new VisionAPI.VisionServiceClient(Properties.Settings.Default.VisionAPIKey, Properties.Settings.Default.VisionAPIHost);

            // How often to analyze. 
            _grabber.TriggerAnalysisOnInterval(Properties.Settings.Default.AnalysisInterval);

            // Reset message. 
            MessageArea.Text = "";

            // Record start time, for auto-stop
            _startTime = DateTime.Now;

            await _grabber.StartProcessingCameraAsync(CameraList.SelectedIndex);
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await _grabber.StopProcessingAsync();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = 1 - SettingsPanel.Visibility;
        }

        private void RecognizerSetup_Click(object sender, RoutedEventArgs e)
        {
            RecognizerSetupPanel.Visibility = 1 - RecognizerSetupPanel.Visibility;
        }

        private async void CreateUserGroup_Click(object sender, RoutedEventArgs e)
        {
            const string myImageDir = @"C:\Images\";

            try
            {
                ////var personGroupExisits = await _faceClient.GetPersonGroupAsync(_personGroupId);

                ////if (personGroupExisits != null)
                ////{
                ////    await _faceClient.DeletePersonGroupAsync(_personGroupId);
                ////    MessageBox.Show($"Person Group Id: {personGroupExisits.Name} already exists, deleting group.");
                ////    await Task.Delay(1000);
                ////}

                await _faceClient.CreatePersonGroupAsync(_personGroupId, "myself");
                await Task.Delay(1000);

                CreatePersonResult myself = await _faceClient.CreatePersonAsync(_personGroupId, "Rob Mah");
                MessageBox.Show("Group created successfully.");

                var files = Directory.GetFiles(myImageDir, "*.jpg");
                int count = 1;

                foreach (string imagePath in files)
                {
                    
                    using (Stream s = File.OpenRead(imagePath))
                    {
                        await _faceClient.AddPersonFaceAsync(_personGroupId, myself.PersonId, s);
                        MessageBox.Show($"Image {count} of {files.Count()} added.");
                        await Task.Delay(1000);
                    }
                    count++;
                }


            }
            catch (Exception ex)
            {

            }

        }

        private async void FacialRecognitionTraining_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TrainingStatus trainingStatus = null;

                await _faceClient.TrainPersonGroupAsync(_personGroupId);

                while (true)
                {
                    trainingStatus = await _faceClient.GetPersonGroupTrainingStatusAsync(_personGroupId);

                    if (trainingStatus.Status != Status.Running)
                    {
                        break;
                    }

                    await Task.Delay(2000);
                }

            }
            catch (Exception ex)
            {

            }
        }


       
       private async Task AddPanicEmergency(string sadnessValue)

        {
            UserCredentials credentials = new UserCredentials()
            {
                CompanyId = "123",
                UserId = "009999",
                Password = "009999"
            };

            Worker worker = new Worker()
            {
                Credentials = credentials
            };
            
            await worker.Emergency($"Attention, this user needs a coffee stat!");

            await SendPhoneCall(credentials);
        }

        private async Task SendPhoneCall(UserCredentials credentials)
        {
            UserDto user = await _userAppService.Get(credentials.CompanyId, credentials.UserId, credentials.Password);

            var requestPath = "Callout/WorkerGracePeriodCall";

            user.Name = "Kyle";
            user.LastName = "";

            var phoneOptions = new PhoneOptions()
            {
            
                NotificationTypeID = 1,
                CompanyLogin = "1095",
                CompanyID = credentials.CompanyId,
                TargetUserID = credentials.UserId,
                SubjectUserID = credentials.UserId,
                Message = ConfigurationManager.AppSettings["Message"],
                TargetUserLoginId = credentials.UserId,
                SourcePhoneNumber = ConfigurationManager.AppSettings["SourcePhoneNumber"],
                TargetPhoneNumber = ConfigurationManager.AppSettings["TargetPhoneNumber"], //user.PhoneNumber,
                LanguageCode = "en",
                TwilioAPISID = "SKd364fabbbbff1ff9b40418b911018a41",
                TwilioAPISecret = "vmfksJBpR3ol4hkMJ0OTuUIavIjXyRme"
            };

            phoneOptions.CallConnectedURL = $"https://twilio:SdF234fdsfsS23DF57@devivr.slmonitor.com/{requestPath}/?cid={HttpUtility.UrlEncode(phoneOptions.CompanyLogin)}&clid={HttpUtility.UrlEncode(phoneOptions.CompanyID)}&cn={HttpUtility.UrlEncode(phoneOptions.Message)}&tuid={HttpUtility.UrlEncode(user.Id.ToString())}&tulid={HttpUtility.UrlEncode(phoneOptions.TargetUserLoginId)}&tufn={HttpUtility.UrlEncode(user.Name)}&tuln={HttpUtility.UrlEncode(user.LastName)}&suid={HttpUtility.UrlEncode(user.Id.ToString())}&sufn={HttpUtility.UrlEncode(user.Name)}&suln={HttpUtility.UrlEncode(user.LastName)}&lc={HttpUtility.UrlEncode(phoneOptions.LanguageCode)}";

            await _coasterPhoneCall.SendPhoneCall(phoneOptions);

        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Hidden;
            Properties.Settings.Default.Save();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        private FaceAPI.Contract.Face CreateFace(FaceAPI.Contract.FaceRectangle rect)
        {
            return new FaceAPI.Contract.Face
            {
                FaceRectangle = new FaceAPI.Contract.FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private FaceAPI.Contract.Face CreateFace(VisionAPI.Contract.FaceRectangle rect)
        {
            return new FaceAPI.Contract.Face
            {
                FaceRectangle = new FaceAPI.Contract.FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private FaceAPI.Contract.Face CreateFace(Common.Rectangle rect)
        {
            return new FaceAPI.Contract.Face
            {
                FaceRectangle = new FaceAPI.Contract.FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private void MatchAndReplaceFaceRectangles(FaceAPI.Contract.Face[] faces, OpenCvSharp.Rect[] clientRects)
        {
            // Use a simple heuristic for matching the client-side faces to the faces in the
            // results. Just sort both lists left-to-right, and assume a 1:1 correspondence. 

            // Sort the faces left-to-right. 
            var sortedResultFaces = faces
                .OrderBy(f => f.FaceRectangle.Left + 0.5 * f.FaceRectangle.Width)
                .ToArray();

            // Sort the clientRects left-to-right.
            var sortedClientRects = clientRects
                .OrderBy(r => r.Left + 0.5 * r.Width)
                .ToArray();

            // Assume that the sorted lists now corrrespond directly. We can simply update the
            // FaceRectangles in sortedResultFaces, because they refer to the same underlying
            // objects as the input "faces" array. 
            for (int i = 0; i < Math.Min(faces.Length, clientRects.Length); i++)
            {
                // convert from OpenCvSharp rectangles
                OpenCvSharp.Rect r = sortedClientRects[i];
                sortedResultFaces[i].FaceRectangle = new FaceAPI.Contract.FaceRectangle { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height };
            }
        }
    }
}
