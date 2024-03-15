using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.ServiceModel;
using OpenRiaServices;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using OpenRiaServices.DomainServices.Client.PortableWeb;
using OpenRiaServices.DomainServices.Client.Web;

#if SILVERLIGHT
using System.Windows;
#endif

namespace OpenRiaServices.DomainServices.Client
{
    /// <summary>
    /// Default <see cref="DomainClient"/> implementation using WCF
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    public class WebDomainClient<TContract> : WebApiDomainClient where TContract : class
    {
        internal const string QueryPropertyName = "DomainServiceQuery";
        internal const string IncludeTotalCountPropertyName = "DomainServiceIncludeTotalCount";

        private ChannelFactory<TContract> _channelFactory;
        private WcfDomainClientFactory _webDomainClientFactory;
        private readonly bool _usesHttps;
        private IEnumerable<Type> _knownTypes;
        private Uri _serviceUri;
        private bool _initializedFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebDomainClient&lt;TContract&gt;"/> class.
        /// </summary>
        /// <param name="serviceUri">The domain service Uri</param>
        /// <exception cref="ArgumentNullException"> is thrown if <paramref name="serviceUri"/>
        /// is null.
        /// </exception>
        public WebDomainClient(Uri serviceUri)
            : this(serviceUri, /* usesHttps */ false, (WcfDomainClientFactory)null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebDomainClient&lt;TContract&gt;"/> class.
        /// </summary>
        /// <param name="serviceUri">The domain service Uri</param>
        /// <param name="usesHttps">A value indicating whether the client should contact
        /// the service using an HTTP or HTTPS scheme.
        /// </param>
        /// <exception cref="ArgumentNullException"> is thrown if <paramref name="serviceUri"/>
        /// is null.
        /// </exception>
        /// <exception cref="ArgumentException"> is thrown if <paramref name="serviceUri"/>
        /// is absolute and <paramref name="usesHttps"/> is true.
        /// </exception>
        public WebDomainClient(Uri serviceUri, bool usesHttps)
            : this(serviceUri, usesHttps, (WcfDomainClientFactory)null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebDomainClient&lt;TContract&gt;"/> class.
        /// </summary>
        /// <param name="serviceUri">The domain service Uri</param>
        /// <param name="usesHttps">A value indicating whether the client should contact
        /// the service using an HTTP or HTTPS scheme.
        /// </param>
        /// <param name="channelFactory">The channel factory that creates channels to communicate with the server.</param>
        /// <exception cref="ArgumentNullException"> is thrown if <paramref name="serviceUri"/>
        /// is null.
        /// </exception>
        /// <exception cref="ArgumentException"> is thrown if <paramref name="serviceUri"/>
        /// is absolute and <paramref name="usesHttps"/> is true.
        /// </exception>
        [Obsolete("Use constructor taking a WcfDomainClientFactory instead")]
        public WebDomainClient(Uri serviceUri, bool usesHttps, ChannelFactory<TContract> channelFactory)
         : this(serviceUri, usesHttps, (WcfDomainClientFactory)null)
        {
            this._channelFactory = channelFactory;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebDomainClient&lt;TContract&gt;"/> class.
        /// </summary>
        /// <param name="serviceUri">The domain service Uri</param>
        /// <param name="usesHttps">A value indicating whether the client should contact
        /// the service using an HTTP or HTTPS scheme.
        /// </param>
        /// <param name="domainClientFactory">The domain client factory that creates channels to communicate with the server.</param>
        /// <exception cref="ArgumentNullException"> is thrown if <paramref name="serviceUri"/>
        /// is null.
        /// </exception>
        /// <exception cref="ArgumentException"> is thrown if <paramref name="serviceUri"/>
        /// is absolute and <paramref name="usesHttps"/> is true.
        /// </exception>
        public WebDomainClient(Uri serviceUri, bool usesHttps, WcfDomainClientFactory domainClientFactory)
            : this(serviceUri, usesHttps, domainClientFactory, CreateHttpClient(HttpClientHandlerFactory.Create()))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebDomainClient&lt;TContract&gt;"/> class.
        /// </summary>
        /// <param name="serviceUri">The domain service Uri</param>
        /// <param name="usesHttps">A value indicating whether the client should contact
        /// the service using an HTTP or HTTPS scheme.
        /// </param>
        /// <param name="domainClientFactory">The domain client factory that creates channels to communicate with the server.</param>
        /// <param name="httpClient">The <see cref="HttpClient"/> used to communicate with the server.</param>
        /// <exception cref="ArgumentNullException"> is thrown if <paramref name="serviceUri"/>
        /// is null.
        /// </exception>
        /// <exception cref="ArgumentException"> is thrown if <paramref name="serviceUri"/>
        /// is absolute and <paramref name="usesHttps"/> is true.
        /// </exception>
        public WebDomainClient(Uri serviceUri, bool usesHttps, WcfDomainClientFactory domainClientFactory, HttpClient httpClient)
            : base(typeof(TContract), serviceUri, httpClient)
        {
            if (serviceUri == null)
            {
                throw new ArgumentNullException("serviceUri");
            }

#if !SILVERLIGHT
            if (!serviceUri.IsAbsoluteUri)
            {
                // Relative URIs currently only supported on Silverlight
                throw new ArgumentException(OpenRiaServices.DomainServices.Client.Resource.DomainContext_InvalidServiceUri, "serviceUri");
            }
#endif

            this._serviceUri = serviceUri;
            this._usesHttps = usesHttps;
            _webDomainClientFactory = domainClientFactory;

#if SILVERLIGHT
            // The domain client should not be initialized at design time
            if (!System.ComponentModel.DesignerProperties.IsInDesignTool)
            {
                this.Initialize();
            }
#endif
        }

        /// <summary>
        /// Fetch argument types from begin method
        /// </summary>
        /// <param name="operationName"></param>
        /// <returns></returns>
        internal override IDictionary<string, Type> GetTypes(string operationName)
        {
            Dictionary<string, Type> types = new Dictionary<string, Type>();

            MethodInfo beginInvokeMethod = WebDomainClient<TContract>.ResolveBeginMethod(operationName);
            foreach (var p in beginInvokeMethod.GetParameters())
            {
                types.Add(p.Name, p.ParameterType);
                if (p.ParameterType == typeof(System.AsyncCallback))
                    break;
            }
            return types;
        }

        private static MethodInfo ResolveBeginMethod(string operationName)
        {
            MethodInfo m = typeof(TContract).GetMethod("Begin" + operationName);
            if (m == null)
            {
                throw new MissingMethodException(string.Format(CultureInfo.CurrentCulture, Resource.WebDomainClient_OperationDoesNotExist, operationName));
            }
            return m;
        }

        /// <summary>
        /// Gets the absolute path to the domain service.
        /// </summary>
        /// <remarks>
        /// The value returned is either the absolute Uri passed into the constructor, or
        /// an absolute Uri constructed from the relative Uri passed into the constructor.
        /// Relative Uris will be made absolute using the Application Host source.
        /// </remarks>
        public Uri ServiceUri
        {
            get
            {
                // Should this bug be preserved?
                return this._channelFactory?.Endpoint.Address.Uri ?? this._serviceUri;
            }
        }

        /// <summary>
        /// Gets a value that indicates whether the <see cref="DomainClient"/> supports cancellation.
        /// </summary>
        public override bool SupportsCancellation
        {
            get
            {
                return true;
            }
        }

        /// <summary>
        /// Gets whether a secure connection should be used.
        /// </summary>
        public bool UsesHttps
        {
            get
            {
                return this._usesHttps;
            }
        }

        /// <summary>
        /// Gets the <see cref="WcfDomainClientFactory"/> used to create this instance, with fallback to
        /// a new <see cref="WebDomainClientFactory"/> in case it was created manually without using a DomainClientFactory.
        /// </summary>
        private WcfDomainClientFactory WebDomainClientFactory
        {
            get
            {
                return _webDomainClientFactory
#if NETSTANDARD
                    ?? (_webDomainClientFactory = new WebAssemblyDomainClientFactory());
#else
                    ?? (_webDomainClientFactory = new WebDomainClientFactory());
#endif
            }
        }

        /// <summary>
        /// Gets the list of known types.
        /// </summary>
        private IEnumerable<Type> KnownTypes
        {
            get
            {
                if (this._knownTypes == null)
                {
                    // KnownTypes is the set of all types we'll need to serialize,
                    // which is the union of the entity types and the framework
                    // message types
                    List<Type> types = this.EntityTypes.ToList();
                    types.Add(typeof(QueryResult));
                    types.Add(typeof(DomainServiceFault));
                    types.Add(typeof(ChangeSetEntry));
                    types.Add(typeof(EntityOperationType));
                    types.Add(typeof(ValidationResultInfo));

                    this._knownTypes = types;
                }
                return this._knownTypes;
            }
        }

        /// <summary>
        /// Gets the channel factory that is used to create channels for communication 
        /// with the server.
        /// </summary>
        public ChannelFactory<TContract> ChannelFactory
        {
            get
            {
#if SILVERLIGHT
                // Initialization prepares the client for use and will fail at design time
                if (System.ComponentModel.DesignerProperties.IsInDesignTool)
                {
                    throw new InvalidOperationException("Domain operations cannot be started at design time.");
                }
                this.Initialize();
#endif
                if (this._channelFactory == null)
                {
                    this._channelFactory = WebDomainClientFactory.CreateChannelFactory<TContract>(_serviceUri, _usesHttps);
                }

                if (!this._initializedFactory)
                {
                    foreach (OperationDescription op in this._channelFactory.Endpoint.Contract.Operations)
                    {
                        foreach (Type knownType in this.KnownTypes)
                        {
                            op.KnownTypes.Add(knownType);
                        }
                    }

                    this._initializedFactory = true;
                }

                return this._channelFactory;
            }
        }

#if SILVERLIGHT
        /// <summary>
        /// Initializes this domain client
        /// </summary>
        /// <exception cref="InvalidOperationException"> is thrown if the current application
        /// or its host are <c>null</c>.
        /// </exception>
        private void Initialize()
        {
            this.ComposeAbsoluteServiceUri();
        }

        /// <summary>
        /// If the service Uri is relative, this method uses the application
        /// source to create an absolute Uri.
        /// </summary>
        /// <remarks>
        /// If usesHttps in the constructor was true, the Uri will be created using
        /// a https scheme instead.
        /// </remarks>
        private void ComposeAbsoluteServiceUri()
        {
            // if the URI is relative, compose with the source URI
            if (!this._serviceUri.IsAbsoluteUri)
            {
                Application current = Application.Current;

                // Only proceed if we can determine a root uri
                if ((current == null) || (current.Host == null) || (current.Host.Source == null))
                {
                    throw new InvalidOperationException(OpenRiaServices.DomainServices.Client.Resource.DomainClient_UnableToDetermineHostUri);
                }

                string sourceUri = current.Host.Source.AbsoluteUri;
                if (this._usesHttps)
                {
                    // We want to replace a http scheme (everything before the ':' in a Uri) with https.
                    // Doing this via UriBuilder loses the OriginalString. Unfortunately, this leads
                    // the builder to include the original port in the output which is not what we want.
                    // To stay as close to the original Uri as we can, we'll just do some simple string
                    // replacement.
                    //
                    // Desired output: http://my.domain/mySite.aspx -> https://my.domain/mySite.aspx
                    // Builder output: http://my.domain/mySite.aspx -> https://my.domain:80/mySite.aspx
                    //   The actual port is probably 443, but including it increases the cross-domain complexity.
                    if (sourceUri.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
                    {
                        sourceUri = "https:" + sourceUri.Substring(5 /*("http:").Length*/);
                    }
                }

                this._serviceUri = new Uri(new Uri(sourceUri), this._serviceUri);
            }
        }
#endif
    }
}
