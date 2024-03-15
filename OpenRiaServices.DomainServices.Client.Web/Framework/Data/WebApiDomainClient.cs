using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;

namespace OpenRiaServices.DomainServices.Client.PortableWeb
{

    /// <summary>
    /// Source: https://github.com/Daniel-Svensson/OpenRiaPlayground
    /// </summary>
    public class WebApiDomainClient : TaskBasedDomainClient
    {
        class BinaryXmlContent : HttpContent
        {
            private readonly WebApiDomainClient domainClient;
            private readonly string operationName;
            private readonly IDictionary<string, object> parameters;
            private readonly List<ServiceQueryPart> queryOptions;

            public BinaryXmlContent(WebApiDomainClient domainClient,
                string operationName, IDictionary<string, object> parameters, List<ServiceQueryPart> queryOptions)
            {
                this.domainClient = domainClient;
                this.operationName = operationName;
                this.parameters = parameters;
                this.queryOptions = queryOptions;

                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/msbin1");
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                var writer = System.Xml.XmlDictionaryWriter.CreateBinaryWriter(stream);

                // Write message
                var rootNamespace = "http://tempuri.org/";
                bool hasQueryOptions = (queryOptions != null && queryOptions.Count > 0);

                if (hasQueryOptions)
                {
                    writer.WriteStartElement("MessageRoot");
                    writer.WriteStartElement("QueryOptions");
                    foreach (var queryOption in queryOptions)
                    {
                        writer.WriteStartElement("QueryOption");
                        writer.WriteAttributeString("Name", queryOption.QueryOperator);
                        writer.WriteAttributeString("Value", queryOption.Expression);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteStartElement(operationName, rootNamespace); // <OperationName>

                // Write all parameters
                if (parameters != null && parameters.Count > 0)
                {
                    foreach (var param in parameters)
                    {
                        writer.WriteStartElement(param.Key);  // <ParameterName>
                        if (param.Value != null)
                        {
                                var serializer = domainClient.GetSerializer(param.Value.GetType());
                                serializer.WriteObjectContent(writer, param.Value);
                        }
                        else
                        {
                            // Null input
                            writer.WriteAttributeString("i", "nil", "http://www.w3.org/2001/XMLSchema-instance", "true");
                        }
                        writer.WriteEndElement();            // </ParameterName>
                    }
                }

                writer.WriteEndDocument(); // </OperationName> and </MessageRoot> if present
                writer.Flush();
                return Task.CompletedTask;
            }

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }
        }

        private static readonly Dictionary<Type, Dictionary<Type, DataContractSerializer>> s_globalSerializerCache = new Dictionary<Type, Dictionary<Type, DataContractSerializer>>();
        private static readonly DataContractSerializer s_faultSerializer = new DataContractSerializer(typeof(DomainServiceFault));
        private readonly Dictionary<Type, DataContractSerializer> _serializerCache;
        private readonly HttpClient _httpClient;

        protected virtual string EndpointSuffix => "/binary/";

        public WebApiDomainClient(Type serviceInterface, Uri baseUri, HttpMessageHandler handler)
            : this(serviceInterface, baseUri, CreateHttpClient(handler))
        {
        }

        public WebApiDomainClient(Type serviceInterface, Uri baseUri, HttpClient httpClient)
        {
            if (httpClient is null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            ServiceInterfaceType = serviceInterface;
            _httpClient = httpClient;
            _httpClient.BaseAddress = new Uri(baseUri.AbsoluteUri + EndpointSuffix, UriKind.Absolute);

            lock (s_globalSerializerCache)
            {
                if (!s_globalSerializerCache.TryGetValue(serviceInterface, out _serializerCache))
                {
                    _serializerCache = new Dictionary<Type, DataContractSerializer>();
                    s_globalSerializerCache.Add(serviceInterface, _serializerCache);
                }
            }
        }

        private protected static HttpClient CreateHttpClient(HttpMessageHandler handler)
        {
            return new HttpClient(handler, disposeHandler: false);
        }

        internal Type ServiceInterfaceType { get; private set; }

        /// <summary>
        /// Configure the underlying <see cref="HttpClient"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ConfigureHttpClient(Action<HttpClient> configure)
        {
            configure?.Invoke(_httpClient);
        }

        #region Begin*** Methods
        /// <summary>
        /// Method called by the framework to begin an Invoke operation asynchronously. Overrides
        /// should not call the base method.
        /// </summary>
        /// <param name="invokeArgs">The arguments to the Invoke operation.</param>
        /// <param name="callback">The callback to invoke when the invocation has been completed.</param>
        /// <param name="userState">Optional user state associated with this operation.</param>
        /// <returns>
        /// An asynchronous result that identifies this invocation.
        /// </returns>
        protected override async Task<InvokeCompletedResult> InvokeCoreAsync(InvokeArgs invokeArgs, CancellationToken cancellationToken)
        {
            var response = await ExecuteRequestAsync(invokeArgs.OperationName, invokeArgs.HasSideEffects, invokeArgs.Parameters, queryOptions: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            IEnumerable<ValidationResult> validationErrors = null;
            object returnValue = null;

            try
            {
                returnValue = ReadResponse(response, invokeArgs.OperationName, invokeArgs.ReturnType);
            }
            catch (FaultException<DomainServiceFault> fe)
            {
                if (fe.Detail.OperationErrors != null)
                {
                    validationErrors = fe.Detail.GetValidationErrors();
                }
                else
                {
                    throw GetExceptionFromServiceFault(fe.Detail);
                }
            }

            return new InvokeCompletedResult(returnValue, validationErrors ?? Enumerable.Empty<ValidationResult>());
        }

        /// <summary>
        /// Method called by the framework to asynchronously process the specified <see cref="T:OpenRiaServices.DomainServices.Client.EntityChangeSet" />.
        /// Overrides should not call the base method.
        /// </summary>
        /// <param name="changeSet">The <see cref="T:OpenRiaServices.DomainServices.Client.EntityChangeSet" /> to submit to the DomainService.</param>
        /// <param name="callback">The callback to invoke when the submit has been executed.</param>
        /// <param name="userState">Optional user state associated with this operation.</param>
        /// <returns>
        /// An asynchronous result that identifies this submit request.
        /// </returns>
        protected override async Task<SubmitCompletedResult> SubmitCoreAsync(EntityChangeSet changeSet, CancellationToken cancellationToken)
        {
            const string operationName = "SubmitChanges";
            var entries = changeSet.GetChangeSetEntries().ToList();
            var parameters = new Dictionary<string, object>() {
                {"changeSet", entries}
            };

            var response = await ExecuteRequestAsync(operationName, hasSideEffects: true, parameters: parameters, queryOptions: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var returnValue = (IEnumerable<ChangeSetEntry>)ReadResponse(response, operationName, typeof(IEnumerable<ChangeSetEntry>));
                return new SubmitCompletedResult(changeSet, returnValue ?? Enumerable.Empty<ChangeSetEntry>());
            }
            catch (FaultException<DomainServiceFault> fe)
            {
                throw GetExceptionFromServiceFault(fe.Detail);
            }
        }

        /// <summary>
        /// Method called by the framework to begin the asynchronous query operation.
        /// </summary>
        /// <param name="query">The query to invoke.</param>
        /// <param name="callback">The callback to invoke when the query has been executed.</param>
        /// <param name="userState">Optional user state associated with this operation.</param>
        /// <returns>
        /// An asynchronous result that identifies this query.
        /// </returns>
        protected override async Task<QueryCompletedResult> QueryCoreAsync(EntityQuery query, CancellationToken cancellationToken)
        {
            List<ServiceQueryPart> queryOptions = query.Query != null ? QuerySerializer.Serialize(query.Query) : null;

            if (query.IncludeTotalCount)
            {
                queryOptions = queryOptions ?? new List<ServiceQueryPart>();
                queryOptions.Add(new ServiceQueryPart()
                {
                    QueryOperator = "includeTotalCount",
                    Expression = "True"
                });
            }

            var response = await ExecuteRequestAsync(query.QueryName, query.HasSideEffects, query.Parameters, queryOptions, cancellationToken)
                .ConfigureAwait(false);

            IEnumerable<ValidationResult> validationErrors = null;
            try
            {
                var queryType = typeof(QueryResult<>).MakeGenericType(query.EntityType);
                var queryResult = (QueryResult)ReadResponse(response, query.QueryName, queryType);
                if (queryResult != null)
                {
                    return new QueryCompletedResult(
                        queryResult.GetRootResults().Cast<Entity>(),
                        queryResult.GetIncludedResults().Cast<Entity>(),
                        queryResult.TotalCount,
                        Enumerable.Empty<ValidationResult>());
                }
            }
            catch (FaultException<DomainServiceFault> fe)
            {
                if (fe.Detail.OperationErrors != null)
                {
                    validationErrors = fe.Detail.GetValidationErrors();
                }
                else
                {
                    throw GetExceptionFromServiceFault(fe.Detail);
                }
            }

            return new QueryCompletedResult(
                    Enumerable.Empty<Entity>(),
                    Enumerable.Empty<Entity>(),
                /* totalCount */ 0,
                    validationErrors ?? Enumerable.Empty<ValidationResult>());
        }
        #endregion


        #region Private methods for making requests
        /// <summary>
        /// Invokes a web request for the operation defined by the <see cref="WebApiDomainClientAsyncResult"/>
        /// </summary>
        /// <param name="result">The result.</param>
        /// <param name="hasSideEffects">if set to <c>true</c> then the request will always be a POST operation.</param>
        /// <param name="parameters">The parameters.</param>
        /// <param name="queryOptions">The query options.</param>
        private Task<HttpResponseMessage> ExecuteRequestAsync(string operationName, bool hasSideEffects, IDictionary<string, object> parameters,
            List<ServiceQueryPart> queryOptions,
            CancellationToken cancellationToken)
        {
            Task<HttpResponseMessage> response = null;
            // Add parameters to query string for get methods
            if (!hasSideEffects)
            {
                response = GetAsync(operationName, parameters, queryOptions, cancellationToken);
            }
            // It is a POST
            if (response == null)
            {
                response = PostAsync(operationName, parameters, queryOptions, cancellationToken);
            }


            return response;
        }

        /// <summary>
        /// Initiates a POST request for the given operation and return the server respose (as a task).
        /// </summary>
        /// <param name="result">The result object which contains information about which operation was performed.</param>
        /// <param name="parameters">The parameters to the server method, or <c>null</c> if no parameters.</param>
        /// <param name="queryOptions">The query options if any.</param>
        /// <returns></returns>
        private Task<HttpResponseMessage> PostAsync(string operationName, IDictionary<string, object> parameters, List<ServiceQueryPart> queryOptions, CancellationToken cancellationToken)
        {
            // Keep reference to dictionary so that we can dispose of it correctly after the request has been posted
            // otherwise there is a small risk that it will be finalized and that it might corrupt the stream
            return _httpClient.PostAsync(operationName, new BinaryXmlContent(this, operationName, parameters, queryOptions), cancellationToken);

        }

        internal virtual IDictionary<string, Type> GetTypes(string operationName)
        {
            return new Dictionary<string, Type>();
        }

        /// <summary>
        /// Initiates a GET request for the given operation and return the server respose (as a task).
        /// </summary>
        /// <param name="result">The result object which contains information about which operation was performed.</param>
        /// <param name="parameters">The parameters to the server method, or <c>null</c> if no parameters.</param>
        /// <param name="queryOptions">The query options if any.</param>
        /// <returns></returns>
        private Task<HttpResponseMessage> GetAsync(string operationName, IDictionary<string, object> parameters, IList<ServiceQueryPart> queryOptions, CancellationToken cancellationToken)
        {
            int i = 0;
            var uriBuilder = new StringBuilder();
            uriBuilder.Append(operationName);

            // Parameters
            if (parameters != null && parameters.Count > 0)
            {
                var types = GetTypes(operationName);
                foreach (var param in parameters)
                {
                    if (param.Key != null && param.Value != null)
                    {
                        uriBuilder.Append(i++ == 0 ? '?' : '&');
                        uriBuilder.Append(Uri.EscapeDataString(param.Key));
                        uriBuilder.Append("=");
                        var value = ConvertValueToString(param.Value, types.ContainsKey(param.Key) ? types[param.Key] : param.Value.GetType());
                        uriBuilder.Append(Uri.EscapeDataString(value));
                    }
                }
            }

            // Query options
            if (queryOptions != null && queryOptions.Count > 0)
            {
                foreach (var queryPart in queryOptions)
                {
                    uriBuilder.Append(i++ == 0 ? "?$" : "&$");
                    uriBuilder.Append(queryPart.QueryOperator);
                    uriBuilder.Append("=");
                    uriBuilder.Append(HttpUtility.UrlEncode(HttpUtility.UrlEncode(queryPart.Expression)));
                }
            }

            // TODO: Switch to POST if uri becomes to long, we can do so by returning nul ...l
            var uri = uriBuilder.ToString();
            return _httpClient.GetAsync(uri, cancellationToken);
        }
        #endregion

        #region Private methods for reading responses

        /// <summary>
        /// Reads a response from the service and converts it to the specified return type.
        /// </summary>
        /// <param name="result">The result object which contains information about which operation was performed.</param>
        /// <param name="returnType">Type which should be returned.</param>
        /// <returns></returns>
        /// <exception cref="OpenRiaServices.DomainServices.Client.DomainOperationException">On server errors which did not produce expected output</exception>
        /// <exception cref="FaultException{DomainServiceFault}">If server returned a DomainServiceFault</exception>
        private object ReadResponse(HttpResponseMessage response, string operationName, Type returnType)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = string.Format(Resources.DomainClient_UnexpectedHttpStatusCode, (int)response.StatusCode, response.StatusCode);

                if (response.StatusCode == HttpStatusCode.BadRequest)
                    throw new DomainOperationException(message, OperationErrorStatus.NotSupported, (int)response.StatusCode, null);
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new DomainOperationException(message, OperationErrorStatus.Unauthorized, (int)response.StatusCode, null);
                else
                    throw new DomainOperationException(message, OperationErrorStatus.ServerError, (int)response.StatusCode, null);
            }

            var streamTask = response.Content.ReadAsStreamAsync();
            Debug.Assert(streamTask.IsCompleted, "Get/Post should use buffering so tream is ready");
            var stream = streamTask.Result;

            using (var reader = System.Xml.XmlDictionaryReader.CreateBinaryReader(stream, System.Xml.XmlDictionaryReaderQuotas.Max))
            {
                reader.Read();

                // Domain Fault
                if (reader.LocalName == "Fault")
                {
                    throw ReadFaultException(reader, operationName);
                }
                else
                {
                    // Validate that we are no on ****Response node
                    VerifyReaderIsAtNode(reader, operationName, "Response");
                    reader.ReadStartElement(); // Read to next which should be ****Result

                    if (returnType != typeof(void))
                    {
                        // Validate that we are no on ****Result node
                        VerifyReaderIsAtNode(reader, operationName, "Result");

                        return DeserializeResult(returnType, reader);
                    }

                    return null;
                }
            }
        }

        /// <summary>
        /// Deserializes the content of the current node using DataContractSerializer
        /// </summary>
        /// <param name="returnType"></param>
        /// <param name="reader"></param>
        /// <returns></returns>
        protected virtual object DeserializeResult(Type returnType, System.Xml.XmlDictionaryReader reader)
        {
            var serializer = GetSerializer(returnType);
            return serializer.ReadObject(reader, verifyObjectName: false);
        }

        /// <summary>
        /// Verifies the reader is at node with LocalName equal to operationName + postfix.
        /// If the reader is at any other node, then a <see cref="DomainOperationException is thrown"/>
        /// </summary>
        /// <param name="reader">The reader.</param>
        /// <param name="operationName">Name of the operation.</param>
        /// <param name="postfix">The postfix.</param>
        /// <exception cref="OpenRiaServices.DomainServices.Client.DomainOperationException">If reader is not at the expected xml element</exception>
        private static void VerifyReaderIsAtNode(System.Xml.XmlDictionaryReader reader, string operationName, string postfix)
        {
            // localName should be operationName + postfix
            if (!(reader.LocalName.Length == operationName.Length + postfix.Length
                && reader.LocalName.StartsWith(operationName)
                && reader.LocalName.EndsWith(postfix)))
            {
                throw new DomainOperationException(
                    string.Format(Resources.DomainClient_UnexpectedResultContent, operationName + postfix, reader.LocalName)
                    , OperationErrorStatus.ServerError, 0, null);
            }
        }

        /// <summary>
        /// Constructs an exception based on a service fault.
        /// </summary>
        /// <param name="serviceFault">The fault received from a service.</param>
        /// <returns>The constructed exception.</returns>
        private static Exception GetExceptionFromServiceFault(DomainServiceFault serviceFault)
        {
            // Status was OK but there still was a server error. We need to transform
            // the error into the appropriate client exception
            if (serviceFault.IsDomainException)
            {
                return new DomainException(serviceFault.ErrorMessage, serviceFault.ErrorCode, serviceFault.StackTrace);
            }
            else if (serviceFault.ErrorCode == 400)
            {
                return new DomainOperationException(serviceFault.ErrorMessage, OperationErrorStatus.NotSupported, serviceFault.ErrorCode, serviceFault.StackTrace);
            }
            else if (serviceFault.ErrorCode == 401)
            {
                return new DomainOperationException(serviceFault.ErrorMessage, OperationErrorStatus.Unauthorized, serviceFault.ErrorCode, serviceFault.StackTrace);
            }
            else
            {
                // for anything else: map to ServerError
                return new DomainOperationException(serviceFault.ErrorMessage, OperationErrorStatus.ServerError, serviceFault.ErrorCode, serviceFault.StackTrace);
            }
        }

        /// <summary>
        /// Reads a Fault reply from the service.
        /// </summary>
        /// <param name="reader">The reader, which should start at the "Fault" element.</param>
        /// <param name="operationName">Name of the operation.</param>
        /// <returns>A FaultException with the details in the server reply</returns>
        private static FaultException<DomainServiceFault> ReadFaultException(System.Xml.XmlDictionaryReader reader, string operationName)
        {
            FaultCode faultCode;
            FaultReason faultReason;
            List<FaultReasonText> faultReasons = new List<FaultReasonText>();

            reader.ReadStartElement("Fault"); // <Fault>
            reader.ReadStartElement("Code");  // <Code>
            reader.ReadStartElement("Value"); // <Value>
            faultCode = new FaultCode(reader.ReadContentAsString());
            reader.ReadEndElement(); // </Value>
            reader.ReadEndElement(); // </Code>

            reader.ReadStartElement("Reason");
            while (reader.LocalName == "Text")
            {
                var lang = reader.XmlLang;
                reader.ReadStartElement("Text");
                var text = reader.ReadContentAsString();
                reader.ReadEndElement();

                faultReasons.Add(new FaultReasonText(text, lang));
            }
            reader.ReadEndElement(); // </Reason>
            faultReason = new FaultReason(faultReasons);

            reader.ReadStartElement("Detail"); // <Detail>
            var fault = (DomainServiceFault)s_faultSerializer.ReadObject(reader);
            return new FaultException<DomainServiceFault>(fault, faultReason, faultCode, operationName);
        }
        #endregion

        #region Serialization helpers

        public static string ConvertValueToString(object parameter, Type parameterType)
        {
            if (parameterType == null)
            {
                throw new ArgumentNullException("parameterType");
            }
            if (parameterType.GetTypeInfo().IsValueType && parameter == null)
            {
                throw new ArgumentNullException("parameter");
            }

            if (Web.Data.QueryStringConverterExtensions.CanConvert(parameterType))
            {
                return Web.Data.QueryStringConverterExtensions.ConvertValueToString(parameter, parameterType);
            }
            using (MemoryStream ms = new MemoryStream())
            {
                //                new DataContractJsonSerializer(parameterType).WriteObject(ms, parameter);               
                //byte[] result = ms.ToArray();
                //string value = Encoding.UTF8.GetString(result, 0, result.Length);

                // TODO: JsonSerializerSettings , and ensure it is correctly configured
                string value = Newtonsoft.Json.JsonConvert.SerializeObject(parameter, parameterType, new Newtonsoft.Json.JsonSerializerSettings()
                {
                    DateFormatHandling = DateFormatHandling.MicrosoftDateFormat
                });

                return Uri.EscapeDataString(value);
            }
        }

        /// <summary>
        /// Gets a <see cref="DataContractSerializer"/> which can be used to serialized the specified type.
        /// The serializers are cached for performance reasons.
        /// </summary>
        /// <param name="type">type which should be serializable.</param>
        /// <returns>A <see cref="DataContractSerializer"/> which can be used to serialize the type</returns>
        private DataContractSerializer GetSerializer(Type type)
        {
            DataContractSerializer serializer;
            lock (_serializerCache)
            {
                if (!_serializerCache.TryGetValue(type, out serializer))
                {
                    // TODO: ENsure that DateTimeOffset is part of known types 
                    // Unlike other primitive types, the DateTimeOffset structure is not a known type by default, so it must be manually added to the list of known types.

                    serializer = new DataContractSerializer(type, new DataContractSerializerSettings
                    {
                        KnownTypes=EntityTypes,
                        SerializeReadOnlyTypes=true
                    });
                    _serializerCache.Add(type, serializer);
                }
            }

            return serializer;
        }
        #endregion
    }
}
