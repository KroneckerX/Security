// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.Http.Features.Authentication;
using Microsoft.AspNet.Http.Internal;
using Microsoft.AspNet.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNet.Authentication.Twitter
{
    internal class TwitterHandler : AuthenticationHandler<TwitterOptions>
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const string StateCookie = "__TwitterState";
        private const string RequestTokenEndpoint = "https://api.twitter.com/oauth/request_token";
        private const string AuthenticationEndpoint = "https://twitter.com/oauth/authenticate?oauth_token=";
        private const string AccessTokenEndpoint = "https://api.twitter.com/oauth/access_token";

        private readonly HttpClient _httpClient;

        public TwitterHandler(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public override async Task<bool> HandleRequestAsync()
        {
            if (Options.CallbackPath.HasValue && Options.CallbackPath == Request.Path)
            {
                return await InvokeReturnPathAsync();
            }
            return false;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            AuthenticationProperties properties = null;
            var query = Request.Query;
            var protectedRequestToken = Request.Cookies[StateCookie];

            var requestToken = Options.StateDataFormat.Unprotect(protectedRequestToken);

            if (requestToken == null)
            {
                return new AuthenticateResult()
                {
                    Error = new ErrorContext(Context, "Invalid state cookie.")
                };
            }

            properties = requestToken.Properties;

            // REVIEW: see which of these are really errors

            var returnedToken = query["oauth_token"];
            if (StringValues.IsNullOrEmpty(returnedToken))
            {
                Logger.LogWarning("Missing oauth_token");
                return new AuthenticateResult()
                {
                    Ticket = new AuthenticationTicket(properties, Options.AuthenticationScheme)
                };
            }

            if (!string.Equals(returnedToken, requestToken.Token, StringComparison.Ordinal))
            {
                Logger.LogWarning("Unmatched token");
                return new AuthenticateResult()
                {
                    Ticket = new AuthenticationTicket(properties, Options.AuthenticationScheme)
                };
            }

            var oauthVerifier = query["oauth_verifier"];
            if (StringValues.IsNullOrEmpty(oauthVerifier))
            {
                Logger.LogWarning("Missing or blank oauth_verifier");
                return new AuthenticateResult()
                {
                    Ticket = new AuthenticationTicket(properties, Options.AuthenticationScheme)
                };
            }

            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps
            };

            Response.Cookies.Delete(StateCookie, cookieOptions);

            var accessToken = await ObtainAccessTokenAsync(Options.ConsumerKey, Options.ConsumerSecret, requestToken, oauthVerifier);

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, accessToken.UserId, ClaimValueTypes.String, Options.ClaimsIssuer),
                new Claim(ClaimTypes.Name, accessToken.ScreenName, ClaimValueTypes.String, Options.ClaimsIssuer),
                new Claim("urn:twitter:userid", accessToken.UserId, ClaimValueTypes.String, Options.ClaimsIssuer),
                new Claim("urn:twitter:screenname", accessToken.ScreenName, ClaimValueTypes.String, Options.ClaimsIssuer)
            },
            Options.ClaimsIssuer);

            if (Options.SaveTokensAsClaims)
            {
                identity.AddClaim(new Claim("access_token", accessToken.Token, ClaimValueTypes.String, Options.ClaimsIssuer));
            }

            return new AuthenticateResult()
            {
                Ticket = await CreateTicketAsync(identity, properties, accessToken)
            };
        }

        protected virtual async Task<AuthenticationTicket> CreateTicketAsync(ClaimsIdentity identity, AuthenticationProperties properties, AccessToken token)
        {
            var context = new TwitterCreatingTicketContext(Context, token.UserId, token.ScreenName, token.Token, token.TokenSecret)
            {
                Principal = new ClaimsPrincipal(identity),
                Properties = properties
            };

            await Options.Events.CreatingTicket(context);

            if (context.Principal?.Identity == null)
            {
                return null;
            }

            return new AuthenticationTicket(context.Principal, context.Properties, Options.AuthenticationScheme);
        }

        protected override async Task HandleUnauthorizedAsync(ChallengeContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var properties = new AuthenticationProperties(context.Properties);
            if (string.IsNullOrEmpty(properties.RedirectUri))
            {
                properties.RedirectUri = CurrentUri;
            }

            var requestToken = await ObtainRequestTokenAsync(Options.ConsumerKey, Options.ConsumerSecret, BuildRedirectUri(Options.CallbackPath), properties);
            if (requestToken.CallbackConfirmed)
            {
                var twitterAuthenticationEndpoint = AuthenticationEndpoint + requestToken.Token;

                var cookieOptions = new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps
                };

                Response.Cookies.Append(StateCookie, Options.StateDataFormat.Protect(requestToken), cookieOptions);

                var redirectContext = new TwitterRedirectToAuthorizationEndpointContext(
                    Context, Options,
                    properties, twitterAuthenticationEndpoint);
                await Options.Events.RedirectToAuthorizationEndpoint(redirectContext);
                context.CompleteRequest();
            }
            else
            {
                Logger.LogError("requestToken CallbackConfirmed!=true");
            }
        }

        public async Task<bool> InvokeReturnPathAsync()
        {
            var result = await HandleAuthenticateOnceAsync();
            if (result.Error != null)
            {
                return await HandleErrorAsync(result.Error);
            }

            var ticket = result?.Ticket;
            if (ticket == null)
            {
                return await HandleErrorAsync(new ErrorContext(Context, "Invalid return state, unable to redirect."));
            }

            var context = new SigningInContext(Context, ticket)
            {
                SignInScheme = Options.SignInScheme,
                RedirectUri = ticket.Properties.RedirectUri
            };
            ticket.Properties.RedirectUri = null;

            await Options.Events.SigningIn(context);

            if (context.SignInScheme != null && context.Principal != null)
            {
                var signInContext = new SignInContext(context.SignInScheme, context.Principal, context.Properties?.Items);
                await Context.Authentication.SignInAsync(signInContext);
                if (signInContext.IsRequestCompleted)
                {
                    context.CompleteRequest();
                }
            }

            if (!context.IsRequestCompleted && context.RedirectUri != null)
            {
                if (context.Principal == null)
                {
                    // TODO: need to override this error behavior to redirect with query string
                    return await HandleErrorAsync(new ErrorContext(Context, "OAuth Authentication failure.")
                    {
                        ErrorHandlerUri = QueryHelpers.AddQueryString(context.RedirectUri, "error", "access_denied")
                    });
                }
            }

            return context.IsRequestCompleted;
        }

        protected override Task HandleSignOutAsync(SignOutContext context)
        {
            throw new NotSupportedException();
        }

        protected override Task HandleSignInAsync(SignInContext context)
        {
            throw new NotSupportedException();
        }

        protected override Task HandleForbiddenAsync(ChallengeContext context)
        {
            throw new NotSupportedException();
        }

        private async Task<RequestToken> ObtainRequestTokenAsync(string consumerKey, string consumerSecret, string callBackUri, AuthenticationProperties properties)
        {
            Logger.LogVerbose("ObtainRequestToken");

            var nonce = Guid.NewGuid().ToString("N");

            var authorizationParts = new SortedDictionary<string, string>
            {
                { "oauth_callback", callBackUri },
                { "oauth_consumer_key", consumerKey },
                { "oauth_nonce", nonce },
                { "oauth_signature_method", "HMAC-SHA1" },
                { "oauth_timestamp", GenerateTimeStamp() },
                { "oauth_version", "1.0" }
            };

            var parameterBuilder = new StringBuilder();
            foreach (var authorizationKey in authorizationParts)
            {
                parameterBuilder.AppendFormat("{0}={1}&", UrlEncoder.UrlEncode(authorizationKey.Key), UrlEncoder.UrlEncode(authorizationKey.Value));
            }
            parameterBuilder.Length--;
            var parameterString = parameterBuilder.ToString();

            var canonicalizedRequestBuilder = new StringBuilder();
            canonicalizedRequestBuilder.Append(HttpMethod.Post.Method);
            canonicalizedRequestBuilder.Append("&");
            canonicalizedRequestBuilder.Append(UrlEncoder.UrlEncode(RequestTokenEndpoint));
            canonicalizedRequestBuilder.Append("&");
            canonicalizedRequestBuilder.Append(UrlEncoder.UrlEncode(parameterString));

            var signature = ComputeSignature(consumerSecret, null, canonicalizedRequestBuilder.ToString());
            authorizationParts.Add("oauth_signature", signature);

            var authorizationHeaderBuilder = new StringBuilder();
            authorizationHeaderBuilder.Append("OAuth ");
            foreach (var authorizationPart in authorizationParts)
            {
                authorizationHeaderBuilder.AppendFormat(
                    "{0}=\"{1}\", ", authorizationPart.Key, UrlEncoder.UrlEncode(authorizationPart.Value));
            }
            authorizationHeaderBuilder.Length = authorizationHeaderBuilder.Length - 2;

            var request = new HttpRequestMessage(HttpMethod.Post, RequestTokenEndpoint);
            request.Headers.Add("Authorization", authorizationHeaderBuilder.ToString());

            var response = await _httpClient.SendAsync(request, Context.RequestAborted);
            response.EnsureSuccessStatusCode();
            string responseText = await response.Content.ReadAsStringAsync();

            var responseParameters = new FormCollection(FormReader.ReadForm(responseText));
            if (string.Equals(responseParameters["oauth_callback_confirmed"], "true", StringComparison.Ordinal))
            {
                return new RequestToken { Token = Uri.UnescapeDataString(responseParameters["oauth_token"]), TokenSecret = Uri.UnescapeDataString(responseParameters["oauth_token_secret"]), CallbackConfirmed = true, Properties = properties };
            }

            return new RequestToken();
        }

        private async Task<AccessToken> ObtainAccessTokenAsync(string consumerKey, string consumerSecret, RequestToken token, string verifier)
        {
            // https://dev.twitter.com/docs/api/1/post/oauth/access_token

            Logger.LogVerbose("ObtainAccessToken");

            var nonce = Guid.NewGuid().ToString("N");

            var authorizationParts = new SortedDictionary<string, string>
            {
                { "oauth_consumer_key", consumerKey },
                { "oauth_nonce", nonce },
                { "oauth_signature_method", "HMAC-SHA1" },
                { "oauth_token", token.Token },
                { "oauth_timestamp", GenerateTimeStamp() },
                { "oauth_verifier", verifier },
                { "oauth_version", "1.0" },
            };

            var parameterBuilder = new StringBuilder();
            foreach (var authorizationKey in authorizationParts)
            {
                parameterBuilder.AppendFormat("{0}={1}&", UrlEncoder.UrlEncode(authorizationKey.Key), UrlEncoder.UrlEncode(authorizationKey.Value));
            }
            parameterBuilder.Length--;
            var parameterString = parameterBuilder.ToString();

            var canonicalizedRequestBuilder = new StringBuilder();
            canonicalizedRequestBuilder.Append(HttpMethod.Post.Method);
            canonicalizedRequestBuilder.Append("&");
            canonicalizedRequestBuilder.Append(UrlEncoder.UrlEncode(AccessTokenEndpoint));
            canonicalizedRequestBuilder.Append("&");
            canonicalizedRequestBuilder.Append(UrlEncoder.UrlEncode(parameterString));

            var signature = ComputeSignature(consumerSecret, token.TokenSecret, canonicalizedRequestBuilder.ToString());
            authorizationParts.Add("oauth_signature", signature);
            authorizationParts.Remove("oauth_verifier");

            var authorizationHeaderBuilder = new StringBuilder();
            authorizationHeaderBuilder.Append("OAuth ");
            foreach (var authorizationPart in authorizationParts)
            {
                authorizationHeaderBuilder.AppendFormat(
                    "{0}=\"{1}\", ", authorizationPart.Key, UrlEncoder.UrlEncode(authorizationPart.Value));
            }
            authorizationHeaderBuilder.Length = authorizationHeaderBuilder.Length - 2;

            var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint);
            request.Headers.Add("Authorization", authorizationHeaderBuilder.ToString());

            var formPairs = new Dictionary<string, string>()
            {
                { "oauth_verifier", verifier },
            };

            request.Content = new FormUrlEncodedContent(formPairs);

            var response = await _httpClient.SendAsync(request, Context.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("AccessToken request failed with a status code of " + response.StatusCode);
                response.EnsureSuccessStatusCode(); // throw
            }

            var responseText = await response.Content.ReadAsStringAsync();
            var responseParameters = new FormCollection(FormReader.ReadForm(responseText));

            return new AccessToken
            {
                Token = Uri.UnescapeDataString(responseParameters["oauth_token"]),
                TokenSecret = Uri.UnescapeDataString(responseParameters["oauth_token_secret"]),
                UserId = Uri.UnescapeDataString(responseParameters["user_id"]),
                ScreenName = Uri.UnescapeDataString(responseParameters["screen_name"])
            };
        }

        private static string GenerateTimeStamp()
        {
            var secondsSinceUnixEpocStart = DateTime.UtcNow - Epoch;
            return Convert.ToInt64(secondsSinceUnixEpocStart.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        private string ComputeSignature(string consumerSecret, string tokenSecret, string signatureData)
        {
            using (var algorithm = new HMACSHA1())
            {
                algorithm.Key = Encoding.ASCII.GetBytes(
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}&{1}",
                        UrlEncoder.UrlEncode(consumerSecret),
                        string.IsNullOrEmpty(tokenSecret) ? string.Empty : UrlEncoder.UrlEncode(tokenSecret)));
                var hash = algorithm.ComputeHash(Encoding.ASCII.GetBytes(signatureData));
                return Convert.ToBase64String(hash);
            }
        }
    }
}