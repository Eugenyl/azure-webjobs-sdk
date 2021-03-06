﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Generic;
using Microsoft.Azure.WebJobs.Host.Blobs;
using Microsoft.Azure.WebJobs.Host.Blobs.Triggers;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Azure.WebJobs.Host.Queues;
using Microsoft.Azure.WebJobs.Host.Queues.Triggers;
using Microsoft.Azure.WebJobs.Host.Timers;
using Microsoft.Azure.WebJobs.Host.Triggers;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Host.Indexers
{
    internal static class DefaultTriggerBindingProvider
    {
        public static ITriggerBindingProvider Create(INameResolver nameResolver,
            IStorageAccountProvider storageAccountProvider,
            IExtensionTypeLocator extensionTypeLocator,
            IHostIdProvider hostIdProvider,
            IQueueConfiguration queueConfiguration,
            JobHostBlobsConfiguration blobsConfiguration,
            IWebJobsExceptionHandler exceptionHandler,
            IContextSetter<IMessageEnqueuedWatcher> messageEnqueuedWatcherSetter,
            IContextSetter<IBlobWrittenWatcher> blobWrittenWatcherSetter,
            ISharedContextProvider sharedContextProvider,
            IExtensionRegistry extensions,
            SingletonManager singletonManager,
            ILoggerFactory loggerFactory)
        {
            List<ITriggerBindingProvider> innerProviders = new List<ITriggerBindingProvider>();
            innerProviders.Add(new QueueTriggerAttributeBindingProvider(nameResolver, storageAccountProvider,
                queueConfiguration, exceptionHandler, messageEnqueuedWatcherSetter,
                sharedContextProvider, loggerFactory));
            innerProviders.Add(new BlobTriggerAttributeBindingProvider(nameResolver, storageAccountProvider, extensionTypeLocator,
                hostIdProvider, queueConfiguration, blobsConfiguration, exceptionHandler, blobWrittenWatcherSetter,
                messageEnqueuedWatcherSetter, sharedContextProvider, singletonManager, loggerFactory));

            // add any registered extension binding providers
            foreach (ITriggerBindingProvider provider in extensions.GetExtensions(typeof(ITriggerBindingProvider)))
            {
                innerProviders.Add(provider);
            }

            return new CompositeTriggerBindingProvider(innerProviders);
        }
    }
}
