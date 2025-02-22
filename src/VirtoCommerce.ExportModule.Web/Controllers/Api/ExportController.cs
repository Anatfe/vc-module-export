using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.ExportModule.Core;
using VirtoCommerce.ExportModule.Core.Model;
using VirtoCommerce.ExportModule.Core.Services;
using VirtoCommerce.ExportModule.Data.Security;
using VirtoCommerce.ExportModule.Web.BackgroundJobs;
using VirtoCommerce.ExportModule.Web.Model;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.ExportImport.PushNotifications;
using VirtoCommerce.Platform.Core.PushNotifications;
using VirtoCommerce.Platform.Core.Security;

namespace VirtoCommerce.ExportModule.Web.Controllers
{
    [Route("api/export")]
    public class ExportController : Controller
    {
        private readonly IEnumerable<Func<ExportDataRequest, IExportProvider>> _exportProviderFactories;
        private readonly IKnownExportTypesRegistrar _knownExportTypesRegistrar;
        private readonly IUserNameResolver _userNameResolver;
        private readonly IPushNotificationManager _pushNotificationManager;
        private readonly IKnownExportTypesResolver _knownExportTypesResolver;
        private readonly IAuthorizationService _authorizationService;
        private readonly IExportFileStorage _exportFileStorage;

        public ExportController(
            IEnumerable<Func<ExportDataRequest, IExportProvider>> exportProviderFactories,
            IKnownExportTypesRegistrar knownExportTypesRegistrar,
            IUserNameResolver userNameResolver,
            IPushNotificationManager pushNotificationManager,
            IKnownExportTypesResolver knownExportTypesResolver,
            IAuthorizationService authorizationService,
            IExportFileStorage exportFileStorage)
        {
            _exportProviderFactories = exportProviderFactories;
            _knownExportTypesRegistrar = knownExportTypesRegistrar;
            _userNameResolver = userNameResolver;
            _pushNotificationManager = pushNotificationManager;
            _knownExportTypesResolver = knownExportTypesResolver;
            _authorizationService = authorizationService;
            _exportFileStorage = exportFileStorage;
        }

        /// <summary>
        /// Gets the list of types ready to be exported
        /// </summary>
        /// <returns>The list of exported known types</returns>
        [HttpGet]
        [Route("knowntypes")]
        [Authorize(ModuleConstants.Security.Permissions.Access)]
        public ActionResult<ExportedTypeDefinition[]> GetExportedKnownTypes()
        {
            return Ok(_knownExportTypesRegistrar.GetRegisteredTypes());
        }

        /// <summary>
        /// Gets the list of available export providers
        /// </summary>
        /// <returns>The list of export providers</returns>
        [HttpGet]
        [Route("providers")]
        [Authorize(ModuleConstants.Security.Permissions.Access)]
        public ActionResult<IExportProvider[]> GetExportProviders()
        {
            return Ok(_exportProviderFactories.Select(x => x(new ExportDataRequest())).ToArray());
        }

        /// <summary>
        /// Provides generic viewable entities collection based on the request
        /// </summary>
        /// <param name="request">Data request</param>
        /// <returns>Viewable entities search result</returns>
        [HttpPost]
        [Route("data")]
        [Authorize(ModuleConstants.Security.Permissions.Access)]
        public async Task<ActionResult<ExportableSearchResult>> GetData([FromBody] ExportDataRequest request)
        {

            var authorizationResult = await _authorizationService.AuthorizeAsync(User, request.DataQuery, request.ExportTypeName + "ExportDataPolicy");
            if (!authorizationResult.Succeeded)
            {
                return Unauthorized();
            }

            var exportedTypeDefinition = _knownExportTypesResolver.ResolveExportedTypeDefinition(request.ExportTypeName);
            var pagedDataSource = (exportedTypeDefinition.DataSourceFactory ?? throw new ArgumentNullException(nameof(ExportedTypeDefinition.DataSourceFactory))).Create(request.DataQuery);

            pagedDataSource.Fetch();
            var queryResult = pagedDataSource.Items;
            var result = new ExportableSearchResult
            {
                TotalCount = pagedDataSource.GetTotalCount(),
                Results = queryResult.ToList()
            };

            return Ok(result);
        }

        /// <summary>
        /// Starts export task
        /// </summary>
        /// <param name="request">Export task description</param>
        /// <returns>Export task id</returns>
        [HttpPost]
        [Route("run")]
        [Authorize(ModuleConstants.Security.Permissions.Access)]
        public async Task<ActionResult<PlatformExportPushNotification>> RunExport([FromBody] ExportDataRequest request)
        {
            var authorizationResult = await _authorizationService.AuthorizeAsync(User, request.DataQuery, request.ExportTypeName + "ExportDataPolicy");
            if (!authorizationResult.Succeeded)
            {
                return Unauthorized();
            }

            var typeTitle = request.ExportTypeName.LastIndexOf('.') > 0 ?
                            request.ExportTypeName.Substring(request.ExportTypeName.LastIndexOf('.') + 1) : request.ExportTypeName;

            var notification = new ExportPushNotification(_userNameResolver.GetCurrentUserName())
            {
                NotifyType = "PlatformExportPushNotification",
                Title = $"{typeTitle} export",
                Description = "Starting export task..."
            };

            await _pushNotificationManager.SendAsync(notification);

            var jobId = BackgroundJob.Enqueue<ExportJob>(x => x.ExportBackgroundAsync(request, notification, JobCancellationToken.Null, null));
            notification.JobId = jobId;

            return Ok(notification);
        }

        /// <summary>
        /// Attempts to cancel export task
        /// </summary>
        /// <param name="cancellationRequest">Cancellation request with task id</param>
        /// <returns></returns>
        [HttpPost]
        [Route("task/cancel")]
        [Authorize(ModuleConstants.Security.Permissions.Access)]
        public ActionResult CancelExport([FromBody] ExportCancellationRequest cancellationRequest)
        {
            BackgroundJob.Delete(cancellationRequest.JobId);
            return Ok();
        }

        /// <summary>
        /// Downloads file by its name
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("download/{fileName}")]
        [AuthorizeAny(PlatformConstants.Security.Permissions.PlatformExport, ModuleConstants.Security.Permissions.Download)]
        public async Task<ActionResult> DownloadExportFile([FromRoute] string fileName)
        {
            var contentType = MimeTypeResolver.ResolveContentType(fileName);
            var stream = await _exportFileStorage.OpenReadAsync(fileName);

            return File(stream, contentType, fileName);
        }
    }
}
