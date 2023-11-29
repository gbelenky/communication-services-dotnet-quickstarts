﻿using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Communication.Chat;
using Azure.Communication.Identity;
using Azure.Communication.Calling.WindowsClient;
using System.Threading.Tasks;
using Azure.Communication;
using Azure;
using Azure.Core;
using Windows.UI.Popups;
using Windows.UI.Core;
using System.Text.RegularExpressions;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.ApplicationModel.Chat;
using PropertyChangedEventArgs = Azure.Communication.Calling.WindowsClient.PropertyChangedEventArgs;
using ChatMessage = Azure.Communication.Chat.ChatMessage;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace ChatTeamsInteropInlineImageQuickStart
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        //ACS resource connection string i.e = "endpoint=https://your-resource.communication.azure.net/;accesskey=your-access-key";
        private const string connectionString_ = "";
        private CommunicationCall call_;
        private CallTokenCredential token_credential;
        private CallClient call_client;
        private CallAgent call_agent;

        private ChatClient chatClient_;
        private bool keepPolling_;

        private string user_Id_;
        private string user_token_;
        private string thread_Id_;

        public MainPage()
        {
            InitializeComponent();
            call_client = new();
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            CallButton.IsEnabled = false;
            if (!await ValidateInput())
            {
                CallButton.IsEnabled = true;
                return;
            }
            await CreateAndSetUserAndTokenAsync();
            await JoinCallAndSetChatThreadId();
        }

        private async void SendMessageButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessageButton.IsEnabled = false;
            ChatThreadClient chatThreadClient = chatClient_.GetChatThreadClient(thread_Id_);
            _ = await chatThreadClient.SendMessageAsync(TxtMessage.Text);

            TxtMessage.Text = "";
            SendMessageButton.IsEnabled = true;
        }

        private async void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            await SetInCallState(false);
            await call_.HangUpAsync(new HangUpOptions());
        }

        private async Task JoinCallAndSetChatThreadId()
        {

            try
            {
                token_credential = new CallTokenCredential(user_token_);
                CallAgentOptions call_agent_options = new();
                call_agent = await call_client.CreateCallAgentAsync(token_credential, call_agent_options);
            }
            catch
            {
                _ = await new MessageDialog("It was not possible to create call agent. Please check if token is valid.").ShowAsync();
                return;
            }

            //  Join a Teams meeting
            try
            {
                JoinCallOptions joinCallOptions = new();
                TeamsMeetingLinkLocator teamsMeetingLinkLocator = new(TxtTeamsLinkTextBox.Text);
                call_ = await call_agent.JoinAsync(teamsMeetingLinkLocator, joinCallOptions);
            }
            catch
            {
                _ = await new MessageDialog("It was not possible to join the Teams meeting. Please check if Teams Link is valid.").ShowAsync();
                return;
            }

            //  set thread Id
            thread_Id_ = ExtractThreadIdFromTeamsLink();

            //  Set up call callbacks
            call_.StateChanged += Call_OnStateChangedAsync;
        }

        private async void Call_OnStateChangedAsync(object sender, PropertyChangedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                CallStatusTextBlock.Text = "Call state : " + call_.State.ToString();
            });

            switch (call_.State)
            {
                case CallState.Connected:
                    await StartPollingForChatMessages();
                    break;

                case CallState.Disconnected:
                case CallState.None:
                    await SetInCallState(false);
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// Background task that keeps polling for chat messages while the call connection is stablished
        /// </summary>
        private async Task StartPollingForChatMessages()
        {
            CommunicationTokenCredential communicationTokenCredential = new(user_token_);
            chatClient_ = new ChatClient(EndPointFromConnectionString(), communicationTokenCredential);
            await Task.Run(async () =>
            {
                keepPolling_ = true;

                ChatThreadClient chatThreadClient = chatClient_.GetChatThreadClient(thread_Id_);
                int previousTextMessages = 0;
                while (keepPolling_)
                {
                    try
                    {
                        CommunicationUserIdentifier currentUser = new(user_Id_);
                        AsyncPageable<ChatMessage> allMessages = chatThreadClient.GetMessagesAsync();
                        SortedDictionary<long, string> messageList = [];
                        int textMessages = 0;
                        await foreach (ChatMessage message in allMessages)
                        {
                            // Get message attachments that are of type 'image'
                            IEnumerable<ChatAttachment> imageAttachments = message.Content.Attachments.Where(x => x.AttachmentType == ChatAttachmentType.Image);

                            var chatAttachmentImageUris = new List<Uri>();
                            foreach (ChatAttachment imageAttachment in imageAttachments)
                            {
                                chatAttachmentImageUris.Add(imageAttachment.PreviewUri);
                            }

                            if (message.Type == ChatMessageType.Html || message.Type == ChatMessageType.Text)
                            {
                                textMessages++;
                                var userPrefix = message.Sender.Equals(currentUser) ? "[you]:" : "";
                                var strippedMessage = StripHtml(message.Content.Message);
                                var chatAttachments = chatAttachmentImageUris.Count > 0 ? "[Attachments]:\n" + string.Join(",\n", chatAttachmentImageUris) : "";
                                messageList.Add(long.Parse(message.SequenceId), $"{userPrefix}{strippedMessage}\n{chatAttachments}");
                            }
                        }

                        // Update UI just when there are new messages
                        if (textMessages > previousTextMessages)
                        {
                            previousTextMessages = textMessages;
                            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                            {
                                TxtChat.Text = string.Join(Environment.NewLine, messageList.Values.ToList());
                            });

                        }
                        if (!keepPolling_)
                        {
                            return;
                        }

                        await SetInCallState(true);
                        await Task.Delay(3000);
                    }
                    catch (Exception e)
                    {
                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            _ = new MessageDialog($"An error ocurred while fetching messages in PollingChatMessagesAsync(). The application will shutdown. Details : {e.Message}").ShowAsync();
                            throw e;
                        });
                        await SetInCallState(false);
                    }
                }
            });
        }

        private async Task SetInCallState(bool inCallState)
        {
            keepPolling_ = inCallState;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {

                TxtTeamsLinkTextBox.IsEnabled = CallButton.IsEnabled = !inCallState;
                HangupButton.IsEnabled = SendMessageButton.IsEnabled = inCallState;
                if (!inCallState)
                {
                    TxtChat.Text = "";
                }
            });
        }

        #region Support / Helper functions
        /// <summary>
        /// Support function to auto-scroll chat area
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TxtChat_TextChanged(object sender, TextChangedEventArgs e)
        {
            var grid = (Grid)VisualTreeHelper.GetChild(TxtChat, 0);
            for (var i = 0; i <= VisualTreeHelper.GetChildrenCount(grid) - 1; i++)
            {
                object obj = VisualTreeHelper.GetChild(grid, i);
                if (!(obj is ScrollViewer)) continue;
                ((ScrollViewer)obj).ChangeView(0.0f, ((ScrollViewer)obj).ExtentHeight, 1.0f);
                break;
            }
        }

        /// <summary>
        /// Validation for input parameters.
        /// 1. Connection string. Example: "endpoint=https://your-resource.communication.azure.net/;accesskey=your-access-key";
        /// 2. Teams meeting link: Example "https://teams.microsoft.com/l/meetup-join/19:meeting_Nzk5YzNmZThtNWU3OS00YTg4LWJjOWMtOTE2YTUzM3IzYzlh@thread.v2/0?context=%7B%66Tid%22:%2272f977bf-86f1-41af-88ab-2d7cd011db47%22,%22Oid%22:%227bb5b66f-c889-420d-bc64-b34a17799e46%22%7D";
        /// </summary>
        /// <returns></returns>
        private async Task<bool> ValidateInput()
        {
            if (string.IsNullOrEmpty(connectionString_) || !connectionString_.StartsWith("endpoint=https://", StringComparison.OrdinalIgnoreCase) || !connectionString_.Contains(";accesskey=", StringComparison.OrdinalIgnoreCase))
            {
                _ = await new MessageDialog("Please enter a valid azure communication service resource connection string for variable : connectionString_ ").ShowAsync();
                return false;
            }

            if (TxtTeamsLinkTextBox.Text.Trim().Length == 0 || !TxtTeamsLinkTextBox.Text.StartsWith("http"))
            {
                _ = await new MessageDialog("Please enter a valid Teams meeting link.").ShowAsync();
                return false;
            }

            if (!TxtTeamsLinkTextBox.Text.Contains("19:meeting_", StringComparison.OrdinalIgnoreCase) && !TxtTeamsLinkTextBox.Text.Contains("19%3ameeting_", StringComparison.OrdinalIgnoreCase))
            {
                _ = await new MessageDialog("Invalid teams meeting link. Missing meeting id : '19:meeting_' ").ShowAsync();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Support function to extract thread id from teams link
        /// </summary>
        /// <returns></returns>
        private string ExtractThreadIdFromTeamsLink()
        {
            //i.e : https://teams.microsoft.com/l/meetup-join/19:meeting_NTgyN
            string teamsMeetingUrl = TxtTeamsLinkTextBox.Text;
            int startThreadId = teamsMeetingUrl.IndexOf("19:meeting_");
            if (startThreadId < 0)
            {
                startThreadId = teamsMeetingUrl.IndexOf("19%3ameeting_"); // URL encoded cases 
            }
            int endThreadId = teamsMeetingUrl.IndexOf("/", startThreadId);
            return System.Net.WebUtility.UrlDecode(teamsMeetingUrl.Substring(startThreadId, endThreadId - startThreadId));
        }

        /// <summary>
        /// Support function to extract URL from connection string
        /// </summary>
        private Uri EndPointFromConnectionString()
        {
            var url = connectionString_.Replace("endpoint=", "");
            var len = url.IndexOf("/;accesskey=");
            return new Uri(url.Substring(0, len));
        }

        /// <summary>
        /// Support function to create ACS user with chat and VOIP permissions
        /// </summary>
        /// <returns></returns>
        private async Task CreateAndSetUserAndTokenAsync()
        {
            CommunicationIdentityClient communicationIdentityClient = new(connectionString_);
            Response<CommunicationUserIdentifier> user = await communicationIdentityClient.CreateUserAsync();
            IEnumerable<CommunicationTokenScope> scopes = new[] { CommunicationTokenScope.Chat, CommunicationTokenScope.VoIP };
            Response<AccessToken> tokenResponseUser = await communicationIdentityClient.GetTokenAsync(user.Value, scopes);
            user_Id_ = user.Value.Id;
            user_token_ = tokenResponseUser.Value.Token;
        }


        /// <summary>
        /// Support function to remove basic html tags introduced by teams client chat
        /// </summary>
        /// <param name="html"></param>
        /// <returns></returns>
        private string StripHtml(string html)
        {
            var tagsExpression = new Regex(@"</?.+?>");
            return tagsExpression.Replace(html, " ");
        }

        #endregion
    }
}