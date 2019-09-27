﻿/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/openiddict/openiddict-core for more information concerning
 * the license and the contributors participating to this project.
 */

using System;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using Properties = OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreConstants.Properties;

namespace OpenIddict.Server.AspNetCore
{
    /// <summary>
    /// Provides the logic necessary to extract, validate and handle OpenID Connect requests.
    /// </summary>
    public class OpenIddictServerAspNetCoreHandler : AuthenticationHandler<OpenIddictServerAspNetCoreOptions>,
        IAuthenticationRequestHandler,
        IAuthenticationSignInHandler,
        IAuthenticationSignOutHandler
    {
        private readonly IOpenIddictServerProvider _provider;

        /// <summary>
        /// Creates a new instance of the <see cref="OpenIddictServerAspNetCoreHandler"/> class.
        /// </summary>
        public OpenIddictServerAspNetCoreHandler(
            [NotNull] IOpenIddictServerProvider provider,
            [NotNull] IOptionsMonitor<OpenIddictServerAspNetCoreOptions> options,
            [NotNull] ILoggerFactory logger,
            [NotNull] UrlEncoder encoder,
            [NotNull] ISystemClock clock)
            : base(options, logger, encoder, clock)
            => _provider = provider;

        public async Task<bool> HandleRequestAsync()
        {
            // Note: the transaction may be already attached when replaying an ASP.NET Core request
            // (e.g when using the built-in status code pages middleware with the re-execute mode).
            var transaction = Context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
            if (transaction == null)
            {
                // Create a new transaction and attach the HTTP request to make it available to the ASP.NET Core handlers.
                transaction = await _provider.CreateTransactionAsync();
                transaction.Properties[typeof(HttpRequest).FullName] = new WeakReference<HttpRequest>(Request);

                // Attach the OpenIddict server transaction to the ASP.NET Core features
                // so that it can retrieved while performing sign-in/sign-out operations.
                Context.Features.Set(new OpenIddictServerAspNetCoreFeature { Transaction = transaction });
            }

            var context = new ProcessRequestContext(transaction);
            await _provider.DispatchAsync(context);

            if (context.IsRequestHandled)
            {
                return true;
            }

            else if (context.IsRequestSkipped)
            {
                return false;
            }

            else if (context.IsRejected)
            {
                var notification = new ProcessErrorContext(transaction)
                {
                    Response = new OpenIddictResponse
                    {
                        Error = context.Error ?? Errors.InvalidRequest,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    }
                };

                await _provider.DispatchAsync(notification);

                if (notification.IsRequestHandled)
                {
                    return true;
                }

                else if (notification.IsRequestSkipped)
                {
                    return false;
                }

                throw new InvalidOperationException(new StringBuilder()
                    .Append("The OpenID Connect response was not correctly processed. This may indicate ")
                    .Append("that the event handler responsible of processing OpenID Connect responses ")
                    .Append("was not registered or was explicitly removed from the handlers list.")
                    .ToString());
            }

            return false;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var transaction = Context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
            if (transaction == null)
            {
                throw new InvalidOperationException("An identity cannot be extracted from this request.");
            }

            if (transaction.Properties.TryGetValue(OpenIddictServerConstants.Properties.AmbientPrincipal, out var principal))
            {
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    (ClaimsPrincipal) principal,
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        protected override async Task HandleChallengeAsync([CanBeNull] AuthenticationProperties properties)
        {
            var transaction = Context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
            if (transaction == null)
            {
                throw new InvalidOperationException("An OpenID Connect response cannot be returned from this endpoint.");
            }

            var context = new ProcessChallengeContext(transaction)
            {
                Response = new OpenIddictResponse
                {
                    Error = GetProperty(properties, Properties.Error),
                    ErrorDescription = GetProperty(properties, Properties.ErrorDescription),
                    ErrorUri = GetProperty(properties, Properties.ErrorUri)
                }
            };

            await _provider.DispatchAsync(context);

            if (context.IsRequestHandled || context.IsRequestSkipped)
            {
                return;
            }

            else if (context.IsRejected)
            {
                var notification = new ProcessErrorContext(transaction)
                {
                    Response = new OpenIddictResponse
                    {
                        Error = context.Error ?? Errors.InvalidRequest,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    }
                };

                await _provider.DispatchAsync(notification);

                if (notification.IsRequestHandled || context.IsRequestSkipped)
                {
                    return;
                }

                throw new InvalidOperationException(new StringBuilder()
                    .Append("The OpenID Connect response was not correctly processed. This may indicate ")
                    .Append("that the event handler responsible of processing OpenID Connect responses ")
                    .Append("was not registered or was explicitly removed from the handlers list.")
                    .ToString());
            }

            static string GetProperty(AuthenticationProperties properties, string name)
                => properties != null && properties.Items.TryGetValue(name, out string value) ? value : null;
        }

        protected override Task HandleForbiddenAsync([CanBeNull] AuthenticationProperties properties)
            => HandleChallengeAsync(properties);

        public async Task SignInAsync([NotNull] ClaimsPrincipal user, [CanBeNull] AuthenticationProperties properties)
        {
            if (user == null)
            {
                throw new ArgumentNullException(nameof(user));
            }

            var transaction = Context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
            if (transaction == null)
            {
                throw new InvalidOperationException("An OpenID Connect response cannot be returned from this endpoint.");
            }

            var context = new ProcessSigninContext(transaction)
            {
                Principal = user,
                Response = new OpenIddictResponse()
            };

            await _provider.DispatchAsync(context);

            if (context.IsRequestHandled || context.IsRequestSkipped)
            {
                return;
            }

            else if (context.IsRejected)
            {
                var notification = new ProcessErrorContext(transaction)
                {
                    Response = new OpenIddictResponse
                    {
                        Error = context.Error ?? Errors.InvalidRequest,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    }
                };

                await _provider.DispatchAsync(notification);

                if (notification.IsRequestHandled || context.IsRequestSkipped)
                {
                    return;
                }

                throw new InvalidOperationException(new StringBuilder()
                    .Append("The OpenID Connect response was not correctly processed. This may indicate ")
                    .Append("that the event handler responsible of processing OpenID Connect responses ")
                    .Append("was not registered or was explicitly removed from the handlers list.")
                    .ToString());
            }
        }

        public async Task SignOutAsync([CanBeNull] AuthenticationProperties properties)
        {
            var transaction = Context.Features.Get<OpenIddictServerAspNetCoreFeature>()?.Transaction;
            if (transaction == null)
            {
                throw new InvalidOperationException("An OpenID Connect response cannot be returned from this endpoint.");
            }

            var context = new ProcessSignoutContext(transaction)
            {
                Response = new OpenIddictResponse()
            };

            await _provider.DispatchAsync(context);

            if (context.IsRequestHandled || context.IsRequestSkipped)
            {
                return;
            }

            else if (context.IsRejected)
            {
                var notification = new ProcessErrorContext(transaction)
                {
                    Response = new OpenIddictResponse
                    {
                        Error = context.Error ?? Errors.InvalidRequest,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    }
                };

                await _provider.DispatchAsync(notification);

                if (notification.IsRequestHandled || context.IsRequestSkipped)
                {
                    return;
                }

                throw new InvalidOperationException(new StringBuilder()
                    .Append("The OpenID Connect response was not correctly processed. This may indicate ")
                    .Append("that the event handler responsible of processing OpenID Connect responses ")
                    .Append("was not registered or was explicitly removed from the handlers list.")
                    .ToString());
            }
        }
    }
}
