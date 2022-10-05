// ------------------------------------------------------------------------
// Copyright 2022 The Dapr Authors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//     http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ------------------------------------------------------------------------

namespace Dapr.Actors
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Dapr.Actors.Communication;
    using Dapr.Actors.Resources;
    using System.Xml;
    using Autogenerated = Dapr.Client.Autogen.Grpc.v1;
    using Grpc.Core;
    using Google.Protobuf;
    using Dapr.Actors.Runtime;
    using Grpc.Net.Client;

    /// <summary>
    /// Class to interact with Dapr runtime over grpc.
    /// </summary>
    internal class DaprGrpcInteractor : IDaprInteractor
    {
        private readonly JsonSerializerOptions jsonSerializerOptions = JsonSerializerDefaults.Web;
        private bool disposed;
        private string daprApiToken;
        private readonly Autogenerated.Dapr.DaprClient client;
        internal Autogenerated.Dapr.DaprClient Client => client;
        private readonly GrpcChannel channel;

        private const string EXCEPTION_HEADER_TAG = "b:KeyValueOfstringbase64Binary";

        public DaprGrpcInteractor(
            GrpcChannel channel,
            Autogenerated.Dapr.DaprClient inner,
            string apiToken)
        {
            this.channel = channel;
            this.client = inner;
            this.daprApiToken = apiToken;
        }

        public async Task<string> GetStateAsync(string actorType, string actorId, string keyName, CancellationToken cancellationToken = default)
        {
            var request = new Autogenerated.GetActorStateRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Key = keyName,
            };
            var options = CreateCallOptions(cancellationToken);

            Autogenerated.GetActorStateResponse response = new Autogenerated.GetActorStateResponse();
            try
            {
                response = await client.GetActorStateAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("GetActorState operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
            return response.Data.ToStringUtf8();
        }

        public async Task SaveStateTransactionallyAsync(string actorType, string actorId, string data, CancellationToken cancellationToken = default, List<Autogenerated.TransactionalActorStateOperation> grpcData = default)
        {
            var request = new Autogenerated.ExecuteActorStateTransactionRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
            };
            request.Operations.AddRange(grpcData);
            var options = CreateCallOptions(cancellationToken);

            try
            {
                await client.ExecuteActorStateTransactionAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("SaveStateTransactionallyAsync operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
        }

        public Task DoStateChangesTransactionallyAsync(DaprStateProvider provider, string actorType, string actorId, IReadOnlyCollection<ActorStateChange> stateChanges, CancellationToken cancellationToken = default)
        {
            return provider.DoStateChangesTransactionallyAsyncGrpc(actorType, actorId, stateChanges, cancellationToken);
        }

        public async Task<IActorResponseMessage> InvokeActorMethodWithRemotingAsync(ActorMessageSerializersManager serializersManager, IActorRequestMessage remotingRequestRequestMessage, CancellationToken cancellationToken = default)
        {
            var requestMessageHeader = remotingRequestRequestMessage.GetHeader();

            var actorId = requestMessageHeader.ActorId.ToString();
            var methodName = requestMessageHeader.MethodName;
            var actorType = requestMessageHeader.ActorType;
            var interfaceId = requestMessageHeader.InterfaceId;

            var serializedHeader = serializersManager.GetHeaderSerializer()
                .SerializeRequestHeader(remotingRequestRequestMessage.GetHeader());

            var msgBodySeriaizer = serializersManager.GetRequestMessageBodySerializer(interfaceId);
            var serializedMsgBody = msgBodySeriaizer.Serialize(remotingRequestRequestMessage.GetBody());

            var request = new Autogenerated.InvokeActorRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Method = methodName,
            };

            if (serializedMsgBody != null)
            {
                request.Data = ByteString.CopyFrom(serializedMsgBody);
            }

            var options = CreateCallOptions(cancellationToken);

            request.Metadata.Add(Constants.RequestHeaderName, Encoding.UTF8.GetString(serializedHeader, 0, serializedHeader.Length));

            var reentrancyId = ActorReentrancyContextAccessor.ReentrancyContext;
            if (reentrancyId != null)
            {
                request.Metadata.Add(Constants.ReentrancyRequestHeaderName, reentrancyId);
            }

            Autogenerated.InvokeActorResponse response = new Autogenerated.InvokeActorResponse();
            try
            {
                response = await client.InvokeActorAsync(request, options);

            }
            catch (RpcException ex)
            {
                throw new DaprApiException("InvokeActorAsync operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }

            IActorResponseMessageHeader actorResponseMessageHeader = null;
            IActorResponseMessageBody actorResponseMessageBody = null;
            if (response != null)
            {
                var responseMessageBody = new MemoryStream(response.Data.ToArray());

                var responseBodySerializer = serializersManager.GetResponseMessageBodySerializer(interfaceId);
                try {
                    actorResponseMessageBody = responseBodySerializer.Deserialize(responseMessageBody);
                }
                catch 
                {
                    var isDeserialzied = 
                        ActorInvokeException.ToException(
                            responseMessageBody,
                            out var remoteMethodException);
                    if (isDeserialzied)
                    {
                        throw new ActorMethodInvocationException(
                            "Remote Actor Method Exception,  DETAILS: " + remoteMethodException.Message,
                            remoteMethodException,
                            false /* non transient */);
                    }
                    else
                    {
                        throw new ActorInvokeException(remoteMethodException.GetType().FullName, string.Format(
                            CultureInfo.InvariantCulture,
                            SR.ErrorDeserializationFailure,
                            remoteMethodException.ToString()));
                    }
                }

            }

            return new ActorResponseMessage(actorResponseMessageHeader, actorResponseMessageBody);
        }

        private string GetExceptionDetails(string header) {
            XmlDocument xmlHeader = new XmlDocument();
            xmlHeader.LoadXml(header);
            XmlNodeList exceptionValueXML = xmlHeader.GetElementsByTagName(EXCEPTION_HEADER_TAG);
            string exceptionDetails = "";
            if (exceptionValueXML != null && exceptionValueXML.Item(1) != null)
            {
                exceptionDetails = exceptionValueXML.Item(1).LastChild.InnerText;
            }
            var base64EncodedBytes = System.Convert.FromBase64String(exceptionDetails);
            return Encoding.UTF8.GetString(base64EncodedBytes);
        }

        public async Task<Stream> InvokeActorMethodWithoutRemotingAsync(string actorType, string actorId, string methodName, string jsonPayload, CancellationToken cancellationToken = default)
        {
            var request = new Autogenerated.InvokeActorRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Method = methodName,
            };

            if (jsonPayload != null)
            {
                request.Data = ByteString.CopyFromUtf8(jsonPayload);
            }

            var options = CreateCallOptions(cancellationToken);

            var reentrancyId = ActorReentrancyContextAccessor.ReentrancyContext;
            if (reentrancyId != null)
            {
                options.Headers.Add(Constants.ReentrancyRequestHeaderName, reentrancyId);
            }

            Autogenerated.InvokeActorResponse response = new Autogenerated.InvokeActorResponse();
            try
            {
                response = await client.InvokeActorAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("InvokeActor operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
            return new MemoryStream(response.Data.ToArray());
        }

        public async Task RegisterReminderAsync(string actorType, string actorId, string reminderName, string data, CancellationToken cancellationToken = default)
        {

            var reminderdata = await ReminderInfo.DeserializeAsync(new MemoryStream(Encoding.UTF8.GetBytes(data)));

            var request = new Autogenerated.RegisterActorReminderRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Name = reminderName,
                DueTime = reminderdata.DueTime != null ? ConverterUtils.ConvertTimeSpanValueInDaprFormat(reminderdata.DueTime) : "",
                Ttl =  reminderdata.Ttl != null ? ConverterUtils.ConvertTimeSpanValueInDaprFormat(reminderdata.Ttl) : "",
                Period =  reminderdata.Period != null ? ConverterUtils.ConvertTimeSpanValueInDaprFormat(reminderdata.Period) : "",
                Data = ByteString.CopyFrom(reminderdata.Data),
            };
            var options = CreateCallOptions(cancellationToken);

            try
            {
                await client.RegisterActorReminderAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("RegisterReminde operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
        }

        public async Task UnregisterReminderAsync(string actorType, string actorId, string reminderName, CancellationToken cancellationToken = default)
        {

            var request = new Autogenerated.UnregisterActorReminderRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Name = reminderName,
            };
            var options = CreateCallOptions(cancellationToken);

            try
            {
                await client.UnregisterActorReminderAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("UnregisterReminder operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
        }

        public async Task RegisterTimerAsync(string actorType, string actorId, string timerName, string data, CancellationToken cancellationToken = default)
        {
            var timerdata = JsonSerializer.Deserialize<TimerInfo>(data, jsonSerializerOptions);

            var request = new Autogenerated.RegisterActorTimerRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Name = timerName,
                DueTime = timerdata.DueTime != null ? ConverterUtils.ConvertTimeSpanValueInDaprFormat(timerdata.DueTime) : "",
                Ttl =  timerdata.Ttl != null ? ConverterUtils.ConvertTimeSpanValueInDaprFormat(timerdata.Ttl) : "",
                Period =  timerdata.Period != null ? ConverterUtils.ConvertTimeSpanValueInDaprFormat(timerdata.Period) : "",
                Data = ByteString.CopyFrom(timerdata.Data),
                Callback = timerdata.Callback
            };
            var options = CreateCallOptions(cancellationToken);

            try
            {
                await client.RegisterActorTimerAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("RegisterActorTimer operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
        }

        public async Task UnregisterTimerAsync(string actorType, string actorId, string timerName, CancellationToken cancellationToken = default)
        {
            var request = new Autogenerated.UnregisterActorTimerRequest()
            {
                ActorId = actorId,
                ActorType = actorType,
                Name = timerName,
            };
            var options = CreateCallOptions(cancellationToken);

            try
            {
                await client.UnregisterActorTimerAsync(request, options);
            }
            catch (RpcException ex)
            {
                throw new DaprApiException("UnregisterActorTimer operation failed: the Dapr endpoint indicated a failure. See InnerException for details.", ex);
            }
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        /// <param name="disposing">False values indicates the method is being called by the runtime, true value indicates the method is called by the user code.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    this.channel.Dispose();
                }

                this.disposed = true;
            }
        }
        
        private CallOptions CreateCallOptions(CancellationToken cancellationToken)
        {
            var options = new CallOptions(headers: new Metadata(), cancellationToken: cancellationToken);

            // add token for dapr api token based authentication
            if (this.daprApiToken is not null)
            {
                options.Headers.Add("dapr-api-token", this.daprApiToken);
            }

            return options;
        }

    }
}
