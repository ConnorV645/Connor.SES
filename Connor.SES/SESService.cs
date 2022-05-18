using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Mail;
using System.Threading.Tasks;
using Amazon;
using Amazon.Extensions.NETCore.Setup;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Microsoft.Extensions.Logging;

namespace Connor.SES
{
    public class SESHelperService<T, B>
        where T : EmailInformation<B>, new()
        where B : SendEmailRequest
    {
        protected readonly AmazonSimpleEmailServiceClient awsClient;
        protected readonly ILogger logger;
        protected readonly ConcurrentQueue<B> sendQueue = new();

        protected readonly int rateLimit;
        protected readonly int timeBetweenChecks;

        const int oneSecond = 1000;
        const int thirtySeconds = 30000;

        public string DefaultFromEmail { get; set; }
        public string DefaultFromName { get; set; }

        public event EventHandler<T> OnSuccess;
        public event EventHandler<T> OnFailure;

        public SESHelperService(ILogger logger, AWSOptions options = null, int rateLimit = 20, int timeBetweenChecks = thirtySeconds)
        {
            this.logger = logger;
            this.rateLimit = rateLimit;
            this.timeBetweenChecks = timeBetweenChecks;

            try
            {
                var sesAccess = Environment.GetEnvironmentVariable("SESAccess");
                var sesSecret = Environment.GetEnvironmentVariable("SESSecret");
                var sesRegion = Environment.GetEnvironmentVariable("SESRegion");

                if (options != null && options.Credentials != null)
                {
                    awsClient = new(options.Credentials, options.Region);
                }
                else if (!string.IsNullOrEmpty(sesAccess) && !string.IsNullOrEmpty(sesSecret) && !string.IsNullOrEmpty(sesRegion))
                {
                    var region = RegionEndpoint.GetBySystemName(sesRegion);
                    if (region == null)
                    {
                        throw new Exception("Invalid AWS Region");
                    }

                    awsClient = new(sesAccess, sesSecret, region);
                }
                else
                {
                    var creds = Amazon.Runtime.FallbackCredentialsFactory.GetCredentials();
                    awsClient = new(creds);
                }


                _ = Task.Run(async () => await ProcessQueue());
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Error Starting SES Helper Service");
            }
        }

        protected async Task ProcessQueue()
        {
            DateTime? batchStartTime = null;
            int batchCount = 1;
            while (true)
            {
                if (sendQueue.TryPeek(out var message) && message != null)
                {
                    var info = new T
                    {
                        Request = message
                    };
                    try
                    {
                        if (!batchStartTime.HasValue)
                        {
                            batchStartTime = DateTime.UtcNow;
                        }
                        else
                        {
                            // SES Rate Limit throttle
                            var allowedEmailCount = Math.Ceiling((DateTime.UtcNow - batchStartTime.Value).TotalSeconds) * rateLimit;
                            if (batchCount++ <= allowedEmailCount)
                            {
                                await Task.Delay(oneSecond);
                            }
                        }
                        var response = await awsClient.SendEmailAsync(message);

                        info.Response = response;

                        OnSuccess?.Invoke(this, info);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, $"Error Sending email to {message.Destination.ToAddresses.FirstOrDefault()} from {message.Source}");
                        OnFailure?.Invoke(this, info);
                    }

                    sendQueue.TryDequeue(out _);
                }
                else
                {
                    batchCount = 1;
                    batchStartTime = null;
                    await Task.Delay(timeBetweenChecks);
                }
            }
        }

        public void QueueEmail(string targetEmail, string subject, string body, string fromEmail, string fromName, Func<SendEmailRequest, B> TCreator)
        {
            var finalFrom = fromEmail ?? DefaultFromEmail;
            if (string.IsNullOrWhiteSpace(fromEmail))
            {
                throw new Exception("From Email Is Required");
            }
            if (string.IsNullOrWhiteSpace(targetEmail))
            {
                throw new Exception("Target Email Is Required");
            }

            var finalFromName = fromName ?? DefaultFromName;

            const string utf8 = "UTF-8";
            var message = new SendEmailRequest
            {
                Source = new MailAddress(finalFrom, finalFromName).ToString(),
                Destination = new Destination
                {
                    ToAddresses = new() { targetEmail }
                },
                Message = new Message
                {
                    Subject = new Content(subject),
                    Body = new Body
                    {
                        Html = new Content
                        {
                            Charset = utf8,
                            Data = body
                        }
                    }
                }
            };
            var Tconstruct = TCreator(message);
            sendQueue.Enqueue(Tconstruct);
        }

        public bool IsQueueEmpty() => sendQueue.IsEmpty;

        public async Task WaitForAllToSend()
        {
            while (!sendQueue.IsEmpty)
            {
                await Task.Delay(oneSecond);
            }
        }
    }
}
