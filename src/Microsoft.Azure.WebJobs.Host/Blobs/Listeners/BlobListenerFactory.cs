﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Host.Queues;
using Microsoft.Azure.WebJobs.Host.Queues.Listeners;
using Microsoft.Azure.WebJobs.Host.Storage;
using Microsoft.Azure.WebJobs.Host.Storage.Blob;
using Microsoft.Azure.WebJobs.Host.Storage.Queue;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host.Blobs.Listeners
{
    internal class BlobListenerFactory : IListenerFactory
    {
        private const string SingletonBlobListenerScopeId = "WebJobs.Internal.Blobs";
        private readonly IHostIdProvider _hostIdProvider;
        private readonly IQueueConfiguration _queueConfiguration;
        private readonly JobHostBlobsConfiguration _blobsConfiguration;
        private readonly IWebJobsExceptionHandler _exceptionHandler;
        private readonly IContextSetter<IBlobWrittenWatcher> _blobWrittenWatcherSetter;
        private readonly IContextSetter<IMessageEnqueuedWatcher> _messageEnqueuedWatcherSetter;
        private readonly ISharedContextProvider _sharedContextProvider;
        private readonly ILoggerFactory _loggerFactory;
        private readonly string _functionId;
        private readonly IStorageAccount _hostAccount;
        private readonly IStorageAccount _dataAccount;
        private readonly IStorageBlobContainer _container;
        private readonly IBlobPathSource _input;
        private readonly ITriggeredFunctionExecutor _executor;
        private readonly SingletonManager _singletonManager;

        public BlobListenerFactory(IHostIdProvider hostIdProvider,
            IQueueConfiguration queueConfiguration,
            JobHostBlobsConfiguration blobsConfiguration,
            IWebJobsExceptionHandler exceptionHandler,
            IContextSetter<IBlobWrittenWatcher> blobWrittenWatcherSetter,
            IContextSetter<IMessageEnqueuedWatcher> messageEnqueuedWatcherSetter,
            ISharedContextProvider sharedContextProvider,
            ILoggerFactory loggerFactory,
            string functionId,
            IStorageAccount hostAccount,
            IStorageAccount dataAccount,
            IStorageBlobContainer container,
            IBlobPathSource input,
            ITriggeredFunctionExecutor executor,
            SingletonManager singletonManager)
        {
            _hostIdProvider = hostIdProvider ?? throw new ArgumentNullException(nameof(hostIdProvider));
            _queueConfiguration = queueConfiguration ?? throw new ArgumentNullException(nameof(queueConfiguration));
            _blobsConfiguration = blobsConfiguration ?? throw new ArgumentNullException(nameof(blobsConfiguration));
            _exceptionHandler = exceptionHandler ?? throw new ArgumentNullException(nameof(exceptionHandler));
            _blobWrittenWatcherSetter = blobWrittenWatcherSetter ?? throw new ArgumentNullException(nameof(blobWrittenWatcherSetter));
            _messageEnqueuedWatcherSetter = messageEnqueuedWatcherSetter ?? throw new ArgumentNullException(nameof(messageEnqueuedWatcherSetter));
            _sharedContextProvider = sharedContextProvider ?? throw new ArgumentNullException(nameof(sharedContextProvider));
            _loggerFactory = loggerFactory;
            _functionId = functionId;
            _hostAccount = hostAccount ?? throw new ArgumentNullException(nameof(hostAccount));
            _dataAccount = dataAccount ?? throw new ArgumentNullException(nameof(dataAccount));
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _singletonManager = singletonManager ?? throw new ArgumentNullException(nameof(singletonManager));
        }

        public async Task<IListener> CreateAsync(CancellationToken cancellationToken)
        {
            // Note that these clients are intentionally for the storage account rather than for the dashboard account.
            // We use the storage, not dashboard, account for the blob receipt container and blob trigger queues.
            IStorageQueueClient primaryQueueClient = _hostAccount.CreateQueueClient();
            IStorageBlobClient primaryBlobClient = _hostAccount.CreateBlobClient();

            // Important: We're using the storage account of the function target here, which is the account that the
            // function the listener is for is targeting. This is the account that will be used
            // to read the trigger blob.
            IStorageBlobClient targetBlobClient = _dataAccount.CreateBlobClient();
            IStorageQueueClient targetQueueClient = _dataAccount.CreateQueueClient();

            string hostId = await _hostIdProvider.GetHostIdAsync(cancellationToken);
            string hostBlobTriggerQueueName = HostQueueNames.GetHostBlobTriggerQueueName(hostId);
            IStorageQueue hostBlobTriggerQueue = primaryQueueClient.GetQueueReference(hostBlobTriggerQueueName);

            SharedQueueWatcher sharedQueueWatcher = _sharedContextProvider.GetOrCreateInstance<SharedQueueWatcher>(
                new SharedQueueWatcherFactory(_messageEnqueuedWatcherSetter));

            SharedBlobListener sharedBlobListener = _sharedContextProvider.GetOrCreateInstance<SharedBlobListener>(
                new SharedBlobListenerFactory(hostId, _hostAccount, _exceptionHandler, _blobWrittenWatcherSetter));

            // Register the blob container we wish to monitor with the shared blob listener.
            await RegisterWithSharedBlobListenerAsync(hostId, sharedBlobListener, primaryBlobClient,
                hostBlobTriggerQueue, sharedQueueWatcher, cancellationToken);

            // Create a "bridge" listener that will monitor the blob
            // notification queue and dispatch to the target job function.
            SharedBlobQueueListener sharedBlobQueueListener = _sharedContextProvider.GetOrCreateInstance<SharedBlobQueueListener>(
                new SharedBlobQueueListenerFactory(_hostAccount, sharedQueueWatcher, hostBlobTriggerQueue,
                    _queueConfiguration, _exceptionHandler, _loggerFactory, sharedBlobListener.BlobWritterWatcher));
            var queueListener = new BlobListener(sharedBlobQueueListener);

            // determine which client to use for the poison queue
            // by default this should target the same storage account
            // as the blob container we're monitoring
            var poisonQueueClient = targetQueueClient;
            if (_dataAccount.Type != StorageAccountType.GeneralPurpose ||
                _blobsConfiguration.CentralizedPoisonQueue)
            {
                // use the primary storage account if the centralize flag is true,
                // or if the target storage account doesn't support queues
                poisonQueueClient = primaryQueueClient;
            }

            // Register our function with the shared blob queue listener
            RegisterWithSharedBlobQueueListenerAsync(sharedBlobQueueListener, targetBlobClient, poisonQueueClient);

            // check a flag in the shared context to see if we've created the singleton
            // shared blob listener in this host instance
            object singletonListenerCreated = false;
            if (!_sharedContextProvider.TryGetValue(SingletonBlobListenerScopeId, out singletonListenerCreated))
            {
                // Create a singleton shared blob listener, since we only
                // want a single instance of the blob poll/scan logic to be running
                // across host instances
                var singletonBlobListener = _singletonManager.CreateHostSingletonListener(
                    new BlobListener(sharedBlobListener), SingletonBlobListenerScopeId);
                _sharedContextProvider.SetValue(SingletonBlobListenerScopeId, true);

                return new CompositeListener(singletonBlobListener, queueListener);
            }
            else
            {
                // We've already created the singleton blob listener
                // so just return our queue listener. Note that while we want the
                // blob scan to be singleton, the shared queue listener needs to run
                // on ALL instances so load can be scaled out
                return queueListener;
            }
        }

        private async Task RegisterWithSharedBlobListenerAsync(
            string hostId,
            SharedBlobListener sharedBlobListener,
            IStorageBlobClient blobClient,
            IStorageQueue hostBlobTriggerQueue,
            IMessageEnqueuedWatcher messageEnqueuedWatcher,
            CancellationToken cancellationToken)
        {
            BlobTriggerExecutor triggerExecutor = new BlobTriggerExecutor(hostId, _functionId, _input,
                BlobETagReader.Instance, new BlobReceiptManager(blobClient),
                new BlobTriggerQueueWriter(hostBlobTriggerQueue, messageEnqueuedWatcher));

            await sharedBlobListener.RegisterAsync(_container, triggerExecutor, cancellationToken);
        }

        private void RegisterWithSharedBlobQueueListenerAsync(
            SharedBlobQueueListener sharedBlobQueueListener,
            IStorageBlobClient blobClient,
            IStorageQueueClient queueClient)
        {
            BlobQueueRegistration registration = new BlobQueueRegistration
            {
                Executor = _executor,
                BlobClient = blobClient,
                QueueClient = queueClient
            };

            sharedBlobQueueListener.Register(_functionId, registration);
        }
    }
}
