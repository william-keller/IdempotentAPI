﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Linq;
using System.Reflection;
using IdempotentAPI.Extensions;

namespace IdempotentAPI
{
    public class Idempotency
    {
        private readonly string _headerKeyName;
        private readonly IDistributedCache _distributedCache;
        private readonly int _expireHours;


        private string _idempotencyKey = string.Empty;
        private bool _isPreIdempotencyApplied = false;
        private bool _isPreIdempotencyCacheReturned = false;
        private HashAlgorithm _hashAlgorithm = new SHA256CryptoServiceProvider();


        public Idempotency(
                    IDistributedCache distributedCache,
                    int expireHours,
                    string headerKeyName)
        {
            _distributedCache = distributedCache;
            _expireHours = expireHours;
            _headerKeyName = headerKeyName;
        }

        public Idempotency(
            IDistributedCache distributedCache,
            int expireHours)
        {
            _distributedCache = distributedCache;
            _expireHours = expireHours;
            _headerKeyName = "IdempotencyKey";
        }

        private bool TryGetIdempotencyKey(HttpRequest httpRequest, out string idempotencyKey, out IActionResult errorActionResult)
        {
            idempotencyKey = string.Empty;
            errorActionResult = null;

            // The "headerKeyName" must be provided as a Header:
            if (!httpRequest.Headers.ContainsKey(_headerKeyName))
            {
                errorActionResult = new BadRequestObjectResult($"The Idempotency header key '{_headerKeyName}' is not found");
                return false;
            }

            Microsoft.Extensions.Primitives.StringValues idempotencyKeys;
            if (!httpRequest.Headers.TryGetValue(_headerKeyName, out idempotencyKeys))
            {
                errorActionResult = new BadRequestObjectResult($"The Idempotency header key '{_headerKeyName}' value is not found");
                return false;
            }

            if (idempotencyKeys.Count <= 0)
            {
                errorActionResult = new BadRequestObjectResult($"An Idempotency header value is not found");
                return false;
            }

            if (idempotencyKeys.Count > 1)
            {
                errorActionResult = new BadRequestObjectResult($"Multiple Idempotency keys were found");
                return false;
            }

            idempotencyKey = idempotencyKeys.ToString();
            return true;
        }
        private bool canPerformIdempotency(HttpRequest httpRequest)
        {
            // If distributedCache is not configured
            if (_distributedCache == null)
            {
                throw new Exception("An IDistributedCache is not configured.");
            }

            // Idempotency is applied on Post & Patch Http methods:
            if (httpRequest.Method != HttpMethods.Post
            && httpRequest.Method != HttpMethods.Patch)
            {
                return false;
            }

            // For multiple executions of the PreStep: 
            if (_isPreIdempotencyApplied)
            {
                return false;
            }

            return true;
        }
        private byte[] generateCacheData(ResultExecutedContext context)
        {
            Dictionary<string, object> cacheData = new Dictionary<string, object>();
            // Cache Request params:
            cacheData.Add("Request.Method", context.HttpContext.Request.Method);
            cacheData.Add("Request.Path", (context.HttpContext.Request.Path.HasValue ? context.HttpContext.Request.Path.Value : String.Empty));
            cacheData.Add("Request.QueryString", context.HttpContext.Request.QueryString.ToUriComponent());
            cacheData.Add("Request.DataHash", getRequestsDataHash(context.HttpContext.Request));

            //Cache Response params:
            cacheData.Add("Response.StatusCode", context.HttpContext.Response.StatusCode);
            cacheData.Add("Response.ContentType", context.HttpContext.Response.ContentType);

            Dictionary<string, List<string>> Headers = context.HttpContext.Response.Headers.ToDictionary(h => h.Key, h => h.Value.ToList());
            cacheData.Add("Response.Headers", Headers);


            // 2019-07-05: Response.Body cannot be accessed because its not yet created.
            // We are saving the Context.Result, because based on this the Response.Body is created.
            Dictionary<string, object> resultObjects = new Dictionary<string, object>();
            var contextResult = context.Result;
            resultObjects.Add("ResultType", contextResult.GetType().AssemblyQualifiedName);

            if (contextResult is CreatedAtRouteResult route)
            {
                //CreatedAtRouteResult.CreatedAtRouteResult(string routeName, object routeValues, object value)
                resultObjects.Add("ResultValue", route.Value);
                resultObjects.Add("ResultRouteName", route.RouteName);

                Dictionary<string, string> RouteValues = route.RouteValues.ToDictionary(r => r.Key, r => r.Value.ToString());
                resultObjects.Add("ResultRouteValues", RouteValues);
            }
            else if (contextResult is ObjectResult objectResult)
            {
                if (objectResult.Value.isAnonymousType())
                {
                    resultObjects.Add("ResultValue", Helpers.AnonymousObjectToDictionary(objectResult.Value, Convert.ToString));
                }
                else
                {
                    resultObjects.Add("ResultValue", objectResult.Value);
                }
            }
            else if (contextResult is StatusCodeResult || contextResult is ActionResult)
            {
                // Known types that do not need additonal data
            }
            else
            {
                throw new NotImplementedException($"ApplyPostIdempotency.generateCacheData is not implement for IActionResult type {contextResult.GetType().ToString()}");
            }

            cacheData.Add("Context.Result", resultObjects);


            // Serialize & Compress data:
            return Helpers.Serialize(cacheData);
        }
        private string getRequestsDataHash(HttpRequest httpRequest)
        {
            List<object> requestsData = new List<object>();

            // The Request body:
            if (httpRequest.ContentLength.HasValue
             && httpRequest.Body != null
                && httpRequest.Body.CanRead)
            {
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    httpRequest.Body.Position = 0;
                    httpRequest.Body.CopyTo(memoryStream);
                    requestsData.Add(memoryStream.ToArray());
                }
            }

            // The Form data:
            if (httpRequest.HasFormContentType
                && httpRequest.Form != null)
            {
                requestsData.Add(httpRequest.Form);
            }

            // The request's URL:
            if (httpRequest.Path.HasValue)
            {
                requestsData.Add(httpRequest.Path.ToString());
            }

            return Helpers.GetHash(_hashAlgorithm, JsonConvert.SerializeObject(requestsData));
        }

        /// <summary>
        /// Return the cached response based on the provided idempotencyKey
        /// </summary>
        /// <param name="context"></param>
        public void ApplyPreIdempotency(ActionExecutingContext context)
        {
            Console.WriteLine($"IdempotencyFilterAttribute [Before]: Request for {context.HttpContext.Request.Method}: {context.HttpContext.Request.Path} received ({context.HttpContext.Request.ContentLength ?? 0} bytes)");

            // Check if Idempotency can be applied:
            if (!canPerformIdempotency(context.HttpContext.Request))
            {
                return;
            }

            // Try to get the IdempotencyKey valud from header:
            IActionResult errorActionResult;
            if (!TryGetIdempotencyKey(context.HttpContext.Request, out _idempotencyKey, out errorActionResult))
            {
                context.Result = errorActionResult;
                return;
            }

            // Check if idempotencyKey exists in cache and return value:
            byte[] cacheDataSerialized = _distributedCache.Get(_idempotencyKey);
            Dictionary<string, object> cacheData = (Dictionary<string, object>)Helpers.DeSerialize(cacheDataSerialized);
            if (cacheData != null)
            {
                // 2019-07-06: Evaluate the "Request.DataHash" in order to be sure that the cached response is returned
                //  for the same combination of IdempotencyKey and Request
                string cachedRequestDataHash = cacheData["Request.DataHash"].ToString();
                string currentRequestDataHash = getRequestsDataHash(context.HttpContext.Request);
                if (cachedRequestDataHash != currentRequestDataHash)
                {
                    context.Result = new BadRequestObjectResult($"The Idempotency header key value '{_idempotencyKey}' was used in a different request.");
                    return;
                }

                // Set the StatusCode and Response result (based on the IActionResult type)
                // The response body will be created from a .NET middleware in a following step.
                int ResponseStatusCode = Convert.ToInt32(cacheData["Response.StatusCode"]);

                Dictionary<string, object> resultObjects = (Dictionary<string, object>)cacheData["Context.Result"];
                Type contextResultType = Type.GetType(resultObjects["ResultType"].ToString());
                if (contextResultType == null)
                {
                    throw new NotImplementedException($"ApplyPreIdempotency, ResultType {resultObjects["ResultType"].ToString()} is not recornized");
                }


                // Initialize the IActionResult based on its type:
                if (contextResultType == typeof(CreatedAtRouteResult))
                {
                    object value = resultObjects["ResultValue"];
                    string routeName = (string)resultObjects["ResultRouteName"];
                    Dictionary<string, string> RouteValues = (Dictionary<string, string>)resultObjects["ResultRouteValues"];

                    context.Result = new CreatedAtRouteResult(routeName, RouteValues, value);
                }
                else if (contextResultType.BaseType == typeof(ObjectResult))
                {
                    object value = resultObjects["ResultValue"];
                    ConstructorInfo ctor = contextResultType.GetConstructor(new[] { typeof(object) });
                    if (ctor != null)
                    {
                        context.Result = (IActionResult)ctor.Invoke(new object[] { value });
                    }
                    else
                    {
                        context.Result = new ObjectResult(value) { StatusCode = ResponseStatusCode };
                    }
                }
                else if (contextResultType.BaseType == typeof(StatusCodeResult)
                    || contextResultType.BaseType == typeof(ActionResult))
                {
                    ConstructorInfo ctor = contextResultType.GetConstructor(new Type[] { });
                    if (ctor != null)
                    {
                        context.Result = (IActionResult)ctor.Invoke(new object[] { });
                    }
                }
                else
                {
                    throw new NotImplementedException($"ApplyPreIdempotency is not implement for IActionResult type {contextResultType.ToString()}");
                }

                //TODO: Check how to add custom headers to response:
                // // Add Headers (if does not exist):
                // // https://docs.microsoft.com/en-us/aspnet/core/mvc/controllers/filters?view=aspnetcore-2.2#servicefilterattribute
                // Dictionary<string, List<string>> headerKeyValues = (Dictionary<string, List<string>>)cacheData["Response.Headers"];
                // if (headerKeyValues != null)
                // {
                //     foreach (KeyValuePair<string, List<string>> headerKeyValue in headerKeyValues)
                //     {
                //         if (!context.HttpContext.Response.Headers.ContainsKey(headerKeyValue.Key))
                //         {
                //             context.HttpContext.Response.Headers.Add(headerKeyValue.Key, headerKeyValue.Value.ToArray());
                //         }
                //     }
                // }

                _isPreIdempotencyCacheReturned = true;
            }

            _isPreIdempotencyApplied = true;
        }


        /// <summary>
        /// Cache the Response in relation to the provided idempotencyKey
        /// </summary>
        /// <param name="context"></param>
        public void ApplyPostIdempotency(ResultExecutedContext context)
        {
            Console.WriteLine($"IdempotencyFilterAttribute [After]: Response for {context.HttpContext.Response.StatusCode} sent ({context.HttpContext.Response.ContentLength ?? 0} bytes)");

            if (!_isPreIdempotencyApplied || _isPreIdempotencyCacheReturned)
            {
                return;
            }

            // Generate the data to be cached
            byte[] cacheDataSerialized = generateCacheData(context);

            // Set the expiration of the cache:
            DistributedCacheEntryOptions cacheOptions = new DistributedCacheEntryOptions();
            cacheOptions.AbsoluteExpirationRelativeToNow = new TimeSpan(_expireHours, 0, 0);

            // Save to cache:
            _distributedCache.Set(_idempotencyKey, cacheDataSerialized, cacheOptions);
        }
    }
}