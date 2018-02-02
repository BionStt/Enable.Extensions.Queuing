using System;
using System.Threading;
using System.Threading.Tasks;
using Enable.Extensions.Queuing.Abstractions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Queue;

namespace Enable.Extensions.Queuing.AzureStorage.Internal
{
    public class AzureStorageQueueClient : BaseQueueClient
    {
        private const uint MaximumDequeueCount = 10;

        private readonly AsyncLazy<CloudQueue> _queueFactory;
        private readonly AsyncLazy<CloudQueue> _deadLetterQueueFactory;

        private readonly TimeSpan _visibilityTimeout = TimeSpan.FromMinutes(1);

        public AzureStorageQueueClient(
            string accountName,
            string accountKey,
            string queueName)
        {
            var credentials = new StorageCredentials(accountName, accountKey);
            var storageAccount = new CloudStorageAccount(credentials, useHttps: true);

            var client = storageAccount.CreateCloudQueueClient();

            _queueFactory = new AsyncLazy<CloudQueue>(async () =>
            {
                var queue = client.GetQueueReference(queueName);
                await queue.CreateIfNotExistsAsync();
                return queue;
            });

            _deadLetterQueueFactory = new AsyncLazy<CloudQueue>(async () =>
            {
                var deadLetterQueueName = GetDeadLetterQueueName(queueName);
                var deadLetterQueue = client.GetQueueReference(deadLetterQueueName);
                await deadLetterQueue.CreateIfNotExistsAsync();
                return deadLetterQueue;
            });
        }

        public override Task AbandonAsync(
            IQueueMessage message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var cloudQueueMessage = new CloudQueueMessage(
                message.MessageId,
                message.LeaseId);

            // Azure Storage Queues don't provide a mechanism for abandoning messages.
            // In order to replicate this behaviour, we update the current message with a
            // visibility timeout of zero, i.e. we set the message to be immediately
            // available to other queue consumers.
            return GetQueue().UpdateMessageAsync(
                cloudQueueMessage,
                TimeSpan.FromSeconds(0),
                MessageUpdateFields.Visibility,
                null,
                null,
                cancellationToken);
        }

        public override Task CompleteAsync(
            IQueueMessage message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return GetQueue().DeleteMessageAsync(
                message.MessageId,
                message.LeaseId,
                null,
                null,
                cancellationToken);
        }

        public override async Task<IQueueMessage> DequeueAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // Iterate through the queue until we either have no further messages
            // or we find one which does not exceed the maximum dequeue count.
            while (true)
            {
                var message = await GetQueue().GetMessageAsync(
                    _visibilityTimeout,
                    null,
                    null,
                    cancellationToken);

                // If there are no further messages in the queue, return.
                if (message == null)
                {
                    return null;
                }

                // If the message we've retrieved has exceeded our maximum
                // dequeue count then we consider it a "poison message" and
                // attempt to move the message to the "dead letter" queue.
                if (message.DequeueCount > MaximumDequeueCount)
                {
                    // Add the poisoned message to the dead letter queue. We
                    // initially mark the new message in the dead letter queue
                    // as invisible to give ourselves a chance to attempt to
                    // remove this message from the dead letter queue if there
                    // is an issue removing this from the main queue.
                    var messageDetails = new
                    {
                        Id = message.Id,
                        PopReceipt = message.PopReceipt
                    };

                    await GetDeadLetterQueue().AddMessageAsync(
                        message,
                        null,
                        TimeSpan.FromSeconds(5),
                        null,
                        null,
                        cancellationToken);

                    try
                    {
                        // Try and remove the original message from the main queue.
                        await GetQueue().DeleteMessageAsync(
                            messageDetails.Id,
                            messageDetails.PopReceipt,
                            null,
                            null,
                            cancellationToken);
                    }
                    catch (StorageException)
                    {
                        // Make a best effort attempt to remove the message from the dead letter queue
                        // if we encounter any issues removing the message from the main queue.
                        await GetDeadLetterQueue().DeleteMessageAsync(message);
                    }
                }
                else
                {
                    // Otherwise, return this message.
                    return new AzureStorageQueueMessage(message);
                }
            }
        }

        public override async Task EnqueueAsync(
            IQueueMessage message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var cloudQueueMessage = CloudQueueMessage.CreateCloudQueueMessageFromByteArray(message.Body);

            await GetQueue().AddMessageAsync(
                cloudQueueMessage,
                null,
                null,
                null,
                null,
                cancellationToken);

            message = new AzureStorageQueueMessage(cloudQueueMessage);
        }

        public override Task RenewLockAsync(
            IQueueMessage message,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var cloudQueueMessage = new CloudQueueMessage(
                message.MessageId,
                message.LeaseId);

            return GetQueue().UpdateMessageAsync(
                cloudQueueMessage,
                _visibilityTimeout,
                MessageUpdateFields.Visibility,
                null,
                null,
                cancellationToken);
        }

        public override void Dispose()
        {
        }

        private string GetDeadLetterQueueName(string queueName)
        {
            // Queue names between 3-63 characters consisting of lower case letters, numbers & dashes.
            // Cannot begin or end with a dash, and does not support consecutive dashes.
            // See https://docs.microsoft.com/en-us/rest/api/storageservices/naming-queues-and-metadata.
            return $"{queueName}-dead-letter";
        }

        private CloudQueue GetQueue()
        {
            return _queueFactory.GetAwaiter().GetResult();
        }

        private CloudQueue GetDeadLetterQueue()
        {
            return _deadLetterQueueFactory.GetAwaiter().GetResult();
        }
    }
}
