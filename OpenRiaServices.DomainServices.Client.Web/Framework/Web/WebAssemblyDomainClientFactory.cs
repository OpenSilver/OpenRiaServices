using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Text;

namespace OpenRiaServices.DomainServices.Client.Web
{
    /// <summary>
    /// For connecting to services from Blazor application.
    /// This class is necessary to bypass client initialization logic.
    /// </summary>
    public class WebAssemblyDomainClientFactory : WcfDomainClientFactory
    {
        private readonly MethodInfo _createInstanceMethod;
        private readonly Func<HttpClient> _httpClientFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebAssemblyDomainClientFactory"/> class.
        /// </summary>
        public WebAssemblyDomainClientFactory()
            : this(() => new HttpClient(HttpClientHandlerFactory.Create(), disposeHandler: false))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebAssemblyDomainClientFactory"/> class.
        /// </summary>
        /// <param name="httpClientFactory">
        /// Method creating a new <see cref="HttpClient"/> each time, should never return null
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="httpClientFactory"/> is null.
        /// </exception>
        public WebAssemblyDomainClientFactory(Func<HttpClient> httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _createInstanceMethod = typeof(WebAssemblyDomainClientFactory)
                .GetMethod(nameof(CreateInstance), BindingFlags.NonPublic | BindingFlags.Instance);
        }

        /// <summary>
        /// Returns passed endpoint
        /// </summary>
        /// <param name="endpoint">base endpoint (service uri)</param>
        /// <param name="requiresSecureEndpoint">not used</param>
        /// <returns>endpoint to connect the service</returns>
        protected override EndpointAddress CreateEndpointAddress(Uri endpoint, bool requiresSecureEndpoint)
        {
            return new EndpointAddress(new Uri(endpoint.OriginalString, UriKind.Absolute));
        }

        /// <summary>
        /// Generates a Binding. The result is not used because we do not have full support of WCF in WebAssembly
        /// </summary>
        /// <param name="endpoint">Absolute service URI</param>
        /// <param name="requiresSecureEndpoint"><c>true</c> if communication must be secured, otherwise <c>false</c></param>
        /// <returns>A <see cref="Binding"/></returns>
        protected override Binding CreateBinding(Uri endpoint, bool requiresSecureEndpoint)
        {
            return new CustomBinding();
        }

        private WebDomainClient<TContract> CreateInstance<TContract>(Uri serviceUri, bool requiresSecureEndpoint)
             where TContract : class
        {
            return new WebDomainClient<TContract>(serviceUri, requiresSecureEndpoint, this, _httpClientFactory());
        }
    }
}
