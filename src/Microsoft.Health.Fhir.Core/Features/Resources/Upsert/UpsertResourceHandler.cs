﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Hl7.Fhir.Model;
using MediatR;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Messages.Upsert;
using Microsoft.Health.Fhir.Core.Notifications;

namespace Microsoft.Health.Fhir.Core.Features.Resources.Upsert
{
    public class UpsertResourceHandler : BaseResourceHandler, IRequestHandler<UpsertResourceRequest, UpsertResourceResponse>
    {
        private readonly IMediator _mediator;

        public UpsertResourceHandler(
            IFhirDataStore fhirDataStore,
            Lazy<IConformanceProvider> conformanceProvider,
            IResourceWrapperFactory resourceWrapperFactory,
            IMediator mediator)
            : base(fhirDataStore, conformanceProvider, resourceWrapperFactory)
        {
            EnsureArg.IsNotNull(mediator, nameof(mediator));

            _mediator = mediator;
        }

        public async Task<UpsertResourceResponse> Handle(UpsertResourceRequest message, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(message, nameof(message));

            Resource resource = message.Resource;

            if (await ConformanceProvider.Value.RequireETag(resource.TypeName, cancellationToken) && message.WeakETag == null)
            {
                throw new PreconditionFailedException(string.Format(Core.Resources.IfMatchHeaderRequiredForResource, resource.TypeName));
            }

            bool allowCreate = await ConformanceProvider.Value.CanUpdateCreate(resource.TypeName, cancellationToken);
            bool keepHistory = await ConformanceProvider.Value.CanKeepHistory(resource.TypeName, cancellationToken);

            ResourceWrapper resourceWrapper = CreateResourceWrapper(resource, deleted: false);
            UpsertOutcome result = await FhirDataStore.UpsertAsync(resourceWrapper, message.WeakETag, allowCreate, keepHistory, cancellationToken);
            resource.VersionId = result.Wrapper.Version;

            switch (resource)
            {
                case Subscription s:
                    await _mediator.Publish(new UpsertSubscriptionNotification(s), cancellationToken);
                    break;
                default:
                    await _mediator.Publish(new UpsertResourceNotification(resource), cancellationToken);
                    break;
            }

            return new UpsertResourceResponse(new SaveOutcome(resource, result.OutcomeType));
        }

        protected override void AddResourceCapability(ListedCapabilityStatement statement, ResourceType resourceType)
        {
            statement.TryAddRestInteraction(resourceType, CapabilityStatement.TypeRestfulInteraction.Update);
        }
    }
}
