using Oqtane.Models;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System;
using Oqtane.Documentation;
using Microsoft.Extensions.Caching.Memory;
using Oqtane.Infrastructure;
using Oqtane.Repository;
using Oqtane.Security;
using Microsoft.AspNetCore.Http;
using Oqtane.Enums;
using Oqtane.Shared;
using System.Globalization;
using Oqtane.Extensions;

namespace Oqtane.Services
{
    [PrivateApi("Don't show in the documentation, as everything should use the Interface")]
    public class ServerSiteService : ISiteService
    {
        private readonly ISiteRepository _sites;
        private readonly IPageRepository _pages;
        private readonly IThemeRepository _themes;
        private readonly IPageModuleRepository _pageModules;
        private readonly IModuleDefinitionRepository _moduleDefinitions;
        private readonly ILanguageRepository _languages;
        private readonly IUserPermissions _userPermissions;
        private readonly ISettingRepository _settings;
        private readonly ITenantManager _tenantManager;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly IMemoryCache _cache;
        private readonly IHttpContextAccessor _accessor;

        public ServerSiteService(ISiteRepository sites, IPageRepository pages, IThemeRepository themes, IPageModuleRepository pageModules, IModuleDefinitionRepository moduleDefinitions, ILanguageRepository languages, IUserPermissions userPermissions, ISettingRepository settings, ITenantManager tenantManager, ISyncManager syncManager, ILogManager logger, IMemoryCache cache, IHttpContextAccessor accessor)
        {
            _sites = sites;
            _pages = pages;
            _themes = themes;
            _pageModules = pageModules;
            _moduleDefinitions = moduleDefinitions;
            _languages = languages;
            _userPermissions = userPermissions;
            _settings = settings;
            _tenantManager = tenantManager;
            _syncManager = syncManager;
            _logger = logger;
            _cache = cache;
            _accessor = accessor;
        }

        public async Task<List<Site>> GetSitesAsync()
        {
            List<Site> sites = new List<Site>();
            if (_accessor.HttpContext.User.IsInRole(RoleNames.Host))
            {
                sites = _sites.GetSites().OrderBy(item => item.Name).ToList();
            }
            return await Task.Run(() => sites);
        }

        public async Task<Site> GetSiteAsync(int siteId)
        {
            Site site = null;
            if (!_accessor.HttpContext.User.Identity.IsAuthenticated)
            {
                site = _cache.GetOrCreate($"site:{_accessor.HttpContext.GetAlias().SiteKey}", entry => 
                {
                    entry.SlidingExpiration = TimeSpan.FromMinutes(30);
                    return GetSite(siteId);
                });
            }
            else
            {
                site = GetSite(siteId);
            }
            return await Task.Run(() => site);
        }

        private Site GetSite(int siteid)
        {
            var alias = _tenantManager.GetAlias();
            var site = _sites.GetSite(siteid);
            if (site != null && site.SiteId == alias.SiteId)
            {
                // site settings
                site.Settings = _settings.GetSettings(EntityNames.Site, site.SiteId)
                    .Where(item => !item.IsPrivate || _accessor.HttpContext.User.IsInRole(RoleNames.Admin))
                    .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);

                // populate File Extensions 
                site.ImageFiles = site.Settings.ContainsKey("ImageFiles") && !string.IsNullOrEmpty(site.Settings["ImageFiles"])
                    ? site.Settings["ImageFiles"] : Constants.ImageFiles;
                site.UploadableFiles = site.Settings.ContainsKey("UploadableFiles") && !string.IsNullOrEmpty(site.Settings["UploadableFiles"])
                ? site.Settings["UploadableFiles"] : Constants.UploadableFiles;

                // pages
                List<Setting> settings = _settings.GetSettings(EntityNames.Page).ToList();
                site.Pages = new List<Page>();
                foreach (Page page in _pages.GetPages(site.SiteId))
                {
                    if (!page.IsDeleted && _userPermissions.IsAuthorized(_accessor.HttpContext.User, PermissionNames.View, page.PermissionList) && (Utilities.IsPageModuleVisible(page.EffectiveDate, page.ExpiryDate) || _userPermissions.IsAuthorized(_accessor.HttpContext.User, PermissionNames.Edit, page.PermissionList)))
                    {
                        page.Settings = settings.Where(item => item.EntityId == page.PageId)
                            .Where(item => !item.IsPrivate || _userPermissions.IsAuthorized(_accessor.HttpContext.User, PermissionNames.Edit, page.PermissionList))
                            .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                        site.Pages.Add(page);
                    }
                }

                site.Pages = GetPagesHierarchy(site.Pages);

                // modules
                List<ModuleDefinition> moduledefinitions = _moduleDefinitions.GetModuleDefinitions(site.SiteId).ToList();
                settings = _settings.GetSettings(EntityNames.Module).ToList();
                site.Modules = new List<Module>();
                foreach (PageModule pagemodule in _pageModules.GetPageModules(site.SiteId).Where(pm => !pm.IsDeleted && _userPermissions.IsAuthorized(_accessor.HttpContext.User, PermissionNames.View, pm.Module.PermissionList)))
                {
                    if (Utilities.IsPageModuleVisible(pagemodule.EffectiveDate, pagemodule.ExpiryDate) || _userPermissions.IsAuthorized(_accessor.HttpContext.User, PermissionNames.Edit, pagemodule.Module.PermissionList))
                    {
                        Module module = new Module
                        {
                            SiteId = pagemodule.Module.SiteId,
                            ModuleDefinitionName = pagemodule.Module.ModuleDefinitionName,
                            AllPages = pagemodule.Module.AllPages,
                            PermissionList = pagemodule.Module.PermissionList,
                            CreatedBy = pagemodule.Module.CreatedBy,
                            CreatedOn = pagemodule.Module.CreatedOn,
                            ModifiedBy = pagemodule.Module.ModifiedBy,
                            ModifiedOn = pagemodule.Module.ModifiedOn,
                            DeletedBy = pagemodule.DeletedBy,
                            DeletedOn = pagemodule.DeletedOn,
                            IsDeleted = pagemodule.IsDeleted,

                            PageModuleId = pagemodule.PageModuleId,
                            ModuleId = pagemodule.ModuleId,
                            PageId = pagemodule.PageId,
                            Title = pagemodule.Title,
                            Pane = pagemodule.Pane,
                            Order = pagemodule.Order,
                            ContainerType = pagemodule.ContainerType,
                            EffectiveDate = pagemodule.EffectiveDate,
                            ExpiryDate = pagemodule.ExpiryDate,

                            ModuleDefinition = _moduleDefinitions.FilterModuleDefinition(moduledefinitions.Find(item => item.ModuleDefinitionName == pagemodule.Module.ModuleDefinitionName)),

                            Settings = settings
                            .Where(item => item.EntityId == pagemodule.ModuleId)
                            .Where(item => !item.IsPrivate || _userPermissions.IsAuthorized(_accessor.HttpContext.User, PermissionNames.Edit, pagemodule.Module.PermissionList))
                            .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue)
                        };

                        site.Modules.Add(module);
                    }
                }

                site.Modules = site.Modules.OrderBy(item => item.PageId).ThenBy(item => item.Pane).ThenBy(item => item.Order).ToList();

                // languages
                site.Languages = _languages.GetLanguages(site.SiteId).ToList();
                var defaultCulture = CultureInfo.GetCultureInfo(Constants.DefaultCulture);
                site.Languages.Add(new Language { Code = defaultCulture.Name, Name = defaultCulture.DisplayName, Version = Constants.Version, IsDefault = !site.Languages.Any(l => l.IsDefault) });

                // themes
                site.Themes = _themes.FilterThemes(_themes.GetThemes().ToList());
            }
            else
            {
                if (site != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Get Attempt {SiteId}", siteid);
                    site = null;
                }
            }
            return site;
        }

        public async Task<Site> AddSiteAsync(Site site)
        {
            if (_accessor.HttpContext.User.IsInRole(RoleNames.Host))
            {
                var alias = _tenantManager.GetAlias();
                site = _sites.AddSite(site);
                _syncManager.AddSyncEvent(alias.TenantId, EntityNames.Site, site.SiteId, SyncEventActions.Create);
                _logger.Log(site.SiteId, LogLevel.Information, this, LogFunction.Create, "Site Added {Site}", site);
            }
            else
            {
                site = null;
            }
            return await Task.Run(() => site);
        }

        public async Task<Site> UpdateSiteAsync(Site site)
        {
            if (_accessor.HttpContext.User.IsInRole(RoleNames.Admin))
            {
                var alias = _tenantManager.GetAlias();
                var current = _sites.GetSite(site.SiteId, false);
                if (site.SiteId == alias.SiteId && site.TenantId == alias.TenantId && current != null)
                {
                    site = _sites.UpdateSite(site);
                    _syncManager.AddSyncEvent(alias.TenantId, EntityNames.Site, site.SiteId, SyncEventActions.Update);
                    string action = SyncEventActions.Refresh;
                    if (current.RenderMode != site.RenderMode || current.Runtime != site.Runtime)
                    {
                        action = SyncEventActions.Reload;
                    }
                    _syncManager.AddSyncEvent(alias.TenantId, EntityNames.Site, site.SiteId, action);
                    _logger.Log(site.SiteId, LogLevel.Information, this, LogFunction.Update, "Site Updated {Site}", site);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Put Attempt {Site}", site);
                    site = null;
                }
            }
            else
            {
                site = null;
            }
            return await Task.Run(() => site);
        }

        public async Task DeleteSiteAsync(int siteId)
        {
            if (_accessor.HttpContext.User.IsInRole(RoleNames.Host))
            {
                var alias = _tenantManager.GetAlias();
                var site = _sites.GetSite(siteId);
                if (site != null && site.SiteId == alias.SiteId)
                {
                    _sites.DeleteSite(siteId);
                    _syncManager.AddSyncEvent(alias.TenantId, EntityNames.Site, site.SiteId, SyncEventActions.Delete);
                    _logger.Log(siteId, LogLevel.Information, this, LogFunction.Delete, "Site Deleted {SiteId}", siteId);
                }
                else
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Delete Attempt {SiteId}", siteId);
                }
            }
            await Task.CompletedTask;
        }

        private static List<Page> GetPagesHierarchy(List<Page> pages)
        {
            List<Page> hierarchy = new List<Page>();
            Action<List<Page>, Page> getPath = null;
            getPath = (pageList, page) =>
            {
                IEnumerable<Page> children;
                int level;
                if (page == null)
                {
                    level = -1;
                    children = pages.Where(item => item.ParentId == null);
                }
                else
                {
                    level = page.Level;
                    children = pages.Where(item => item.ParentId == page.PageId);
                }
                foreach (Page child in children)
                {
                    child.Level = level + 1;
                    child.HasChildren = pages.Any(item => item.ParentId == child.PageId && !item.IsDeleted && item.IsNavigation);
                    hierarchy.Add(child);
                    getPath(pageList, child);
                }
            };
            pages = pages.OrderBy(item => item.Order).ToList();
            getPath(pages, null);

            // add any non-hierarchical items to the end of the list
            foreach (Page page in pages)
            {
                if (hierarchy.Find(item => item.PageId == page.PageId) == null)
                {
                    hierarchy.Add(page);
                }
            }
            return hierarchy;
        }

        [Obsolete("This method is deprecated.", false)]
        public void SetAlias(Alias alias)
        {
        }
    }
}
