﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InfoCaster.Umbraco.UrlTracker.Extensions;
using InfoCaster.Umbraco.UrlTracker.Helpers;
using InfoCaster.Umbraco.UrlTracker.Models;
//using umbraco.NodeFactory;
using Umbraco.Core;
//using Umbraco.Core.Composing;
using Umbraco.Web.Composing;
using Umbraco.Core.Models;
using Umbraco.Core.Persistence;
using Umbraco.Web;
using System.Data.SqlClient;
using static InfoCaster.Umbraco.UrlTracker.Helpers.SqlHelper;
using Umbraco.Web.Install.Models;
using NPoco;

namespace InfoCaster.Umbraco.UrlTracker.Repositories
{
    public static class UrlTrackerRepository
    {
        static readonly Uri _baseUri = new Uri("http://www.example.org");
        static List<UrlTrackerModel> _forcedRedirectsCache;
        static DateTime LastForcedRedirectCacheRefreshTime = DateTime.UtcNow;
        static readonly object _cacheLock = new object();
        static readonly object _timeoutCacheLock = new object();
        static readonly string DatabaseProvider = Constants.DatabaseProviders.SqlCe;

        #region Add
        public static bool AddUrlMapping(IContent content, int rootNodeId, string url, AutoTrackingTypes type, bool isChild = false)
        {

            UmbracoHelper umbracoHelper = Current.Factory.GetInstance<UmbracoHelper>();
            if (url != "#" && content.TemplateId > 0)
            {
                string notes = isChild ? "An ancestor" : "This page";
                switch (type)
                {
                    case AutoTrackingTypes.Moved:
                        notes += " was moved";
                        break;
                    case AutoTrackingTypes.Renamed:
                        notes += " was renamed";
                        break;
                    case AutoTrackingTypes.UrlOverwritten:
                        notes += "'s property 'umbracoUrlName' changed";
                        break;
                    case AutoTrackingTypes.UrlOverwrittenSEOMetadata:
                        notes += string.Format("'s property '{0}' changed", UrlTrackerSettings.SEOMetadataPropertyName);
                        break;
                }

                url = UrlTrackerHelper.ResolveShortestUrl(url);

                if (UrlTrackerSettings.HasDomainOnChildNode)
                {
                    var rootUri = new Uri(umbracoHelper.GetUrl(rootNodeId));
                    var shortRootUrl = UrlTrackerHelper.ResolveShortestUrl(rootUri.AbsolutePath);
                    if (url.StartsWith(shortRootUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        url = UrlTrackerHelper.ResolveShortestUrl(url.Substring(shortRootUrl.Length));
                    }
                }

                if (!string.IsNullOrEmpty(url))
                {
                    string query = "SELECT 1 FROM icUrlTracker WHERE RedirectNodeId = @nodeId AND OldUrl = @url";
                    //var SqlParams = new List<SqlHelper.SqlParameter>();
                    //SqlParams.Add(SqlHelper.CreateGenericParameter("nodeId", content.Id));
                    //SqlParams.Add(CreateStringParameter("url", url));
                    int exists = ExecuteScalar<int>(query, new { @nodeId = content.Id, @url = url });
                    if (exists != 1)
                    {
                        LoggingHelper.LogInformation("UrlTracker Repository | Adding mapping for node id: {0} and url: {1}", new string[] { content.Id.ToString(), url });

                        query = "INSERT INTO icUrlTracker (RedirectRootNodeId, RedirectNodeId, OldUrl, Notes) VALUES (@rootNodeId, @nodeId, @url, @notes)";
                        //var SqlInsertParams = new List<SqlHelper.SqlParameter>();
                        //SqlInsertParams.Add(CreateGenericParameter("rootNodeId", rootNodeId));
                        //SqlInsertParams.Add(CreateGenericParameter("nodeId", content.Id));
                        //SqlInsertParams.Add(CreateStringParameter("url", url));
                        //SqlInsertParams.Add(CreateGenericParameter("nodeId", content.Id));
                        //SqlInsertParams.Add(CreateStringParameter("notes", notes));

                        var parameters = new { 
                            @rootNodeId = rootNodeId,
                            @nodeId = content.Id,
                            @url = url,
                            @notes = notes
                        };

                        ExecuteScalar<int>(query, parameters);
                        //if (content.Children().Any())
                        //{
                        //    foreach (IPublishedContent child in content.Children())
                        //    {
                        //        Node node = new Node(child.Id);
                        //        AddUrlMapping(child, rootNodeId, node.NiceUrl, type, true);
                        //    }
                        //}
                        return true;
                    }
                }
            }
            return false;
        }

        public static void AddUrlTrackerEntry(UrlTrackerModel urlTrackerModel)
        {
            string query = "INSERT INTO icUrlTracker (OldUrl, OldUrlQueryString, OldRegex, RedirectRootNodeId, RedirectNodeId, RedirectUrl, RedirectHttpCode, RedirectPassThroughQueryString, ForceRedirect, Notes) VALUES (@oldUrl, @oldUrlQueryString, @oldRegex, @redirectRootNodeId, @redirectNodeId, @redirectUrl, @redirectHttpCode, @redirectPassThroughQueryString, @forceRedirect, @notes)";

            var sqlParams = new {
            oldUrl =  urlTrackerModel.OldUrl,
            oldUrlQueryString =  urlTrackerModel.OldUrlQueryString,
            oldRegex =  urlTrackerModel.OldRegex,
            redirectRootNodeId =  urlTrackerModel.RedirectRootNodeId,
            redirectNodeId =  urlTrackerModel.RedirectNodeId,
            redirectUrl =  urlTrackerModel.RedirectUrl,
            redirectHttpCode =  urlTrackerModel.RedirectHttpCode,
            redirectPassThroughQueryString =  urlTrackerModel.RedirectPassThroughQueryString,
            forceRedirect =  urlTrackerModel.ForceRedirect,
            notes =  urlTrackerModel.Notes};

            ExecuteScalar<int>(query, sqlParams);

            if (urlTrackerModel.ForceRedirect)
                ReloadForcedRedirectsCache();
        }

        public static void AddGoneEntryByNodeId(int nodeId)
        {
            UmbracoHelper umbracoHelper = Current.Factory.GetInstance<UmbracoHelper>();
            if (Current.UmbracoContext == null) // NiceUrl will throw an exception if UmbracoContext is null, and we'll be unable to retrieve the URL of the node
                return;

            string url = umbracoHelper.GetUrl(nodeId);
            if (url == "#")
                return;

            if (url.StartsWith("http://") || url.StartsWith("https://"))
            {
                Uri uri = new Uri(url);
                url = uri.AbsolutePath;
            }
            url = UrlTrackerHelper.ResolveShortestUrl(url);

            string query = "SELECT 1 FROM icUrlTracker WHERE RedirectNodeId = @redirectNodeId AND OldUrl = @oldUrl AND RedirectHttpCode = 410";
            var SqlParams = new
            {
                redirectNodeId = nodeId,
                oldUrl = url
            };

            int exists = ExecuteScalar<int>(query, SqlParams);
            if (exists != 1)
            {
                LoggingHelper.LogInformation("UrlTracker Repository | Inserting 410 Gone mapping for node with id: {0}", nodeId);

                query = "INSERT INTO icUrlTracker (RedirectNodeId, OldUrl, RedirectHttpCode, Notes) VALUES (@redirectNodeId, @oldUrl, 410, @notes)";
                var SqlInsertParams = new
                {
                    redirectNodeId = nodeId,
                    oldUrl = url,
                    notes = "Node removed"
                };
                ExecuteScalar<int>(query, SqlInsertParams);
            }
            else
                LoggingHelper.LogInformation("UrlTracker Repository | Skipping 410 Gone mapping for node with id: {0} (already exists)", nodeId);
        }
        #endregion

        #region Delete
        public static void DeleteUrlTrackerEntriesByNodeId(int nodeId)
        {
            string query = "SELECT 1 FROM icUrlTracker WHERE RedirectNodeId = @nodeId AND RedirectHttpCode != 410";
            var SqlParams = new { nodeId = nodeId };
            int exists = ExecuteScalar<int>(query, SqlParams);
            if (exists == 1)
            {
                LoggingHelper.LogInformation("UrlTracker Repository | Deleting Url Tracker entry of node with id: {0}", nodeId);

                query = "DELETE FROM icUrlTracker WHERE RedirectNodeId = @nodeId AND RedirectHttpCode != 410";
                var SqlDeleteParams = new { nodeId = nodeId };
                ExecuteScalar<int>(query, SqlDeleteParams);
            }

            ReloadForcedRedirectsCache();
        }

        public static void DeleteNotFoundEntriesByOldUrl(string oldUrl)
        {
            string query = "SELECT 1 FROM icUrlTracker WHERE Is404 = 1 AND OldUrl = @oldUrl";
            var SqlParams = new { oldUrl = oldUrl };
            int exists = ExecuteScalar<int>(query, SqlParams);
            if (exists == 1)
            {
                LoggingHelper.LogInformation("UrlTracker Repository | Deleting Not Found entries with OldUrl: {0}", oldUrl);

                query = "DELETE FROM icUrlTracker WHERE Is404 = 1 AND OldUrl = @oldUrl";
                var SqlDeleteParams = new { oldUrl = oldUrl };
                ExecuteScalar<int>(query, SqlDeleteParams);
            }
        }

        public static void DeleteUrlTrackerEntry(int id)
        {
            LoggingHelper.LogInformation("UrlTracker Repository | Deleting Url Tracker entry with id: {0}", id);

            string query = "DELETE FROM icUrlTracker WHERE Id = @id";
            var SqlDeleteParams = new { id = id };
            ExecuteScalar<int>(query, SqlDeleteParams);

            ReloadForcedRedirectsCache();
        }

        public static void ClearNotFoundEntries()
        {
            LoggingHelper.LogInformation("UrlTracker Repository | Clearing all not found entries");

            string query = "DELETE FROM icUrlTracker WHERE Is404 = 1";
            ExecuteScalar<int>(query);
        }

        public static void DeleteNotFoundEntriesByRootAndOldUrl(int redirectRootNodeId, string oldUrl)
        {
            // trigger delete, but without checking if it exists = unneccesary call to database
            LoggingHelper.LogInformation("UrlTracker Repository | Deleting Not Found entries with OldUrl: {0}", oldUrl);

            const string query = "DELETE FROM icUrlTracker WHERE Is404 = 1 AND OldUrl = @oldUrl AND RedirectRootNodeId = @rootId";
            var SqlDeleteParams = new
            {
                oldUrl = oldUrl,
                rootId = redirectRootNodeId
            };
            ExecuteScalar<int>(query, SqlDeleteParams);
        }
        #endregion

        #region Get
        public static UrlTrackerModel GetUrlTrackerEntryById(int id)
        {
            string query = "SELECT * FROM icUrlTracker WHERE Id = @id";

            var connection = new SqlConnection(Current.ScopeProvider.CreateScope().Database.Connection.ConnectionString);
            SqlCommand command = new SqlCommand(query, connection);

            using (SqlDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    return new UrlTrackerModel(
                        (int)reader["Id"],
                        (string)reader["OldUrl"],
                        (string)reader["OldUrlQueryString"],
                        (string)reader["OldRegex"],
                        (int)reader["RedirectRootNodeId"],
                        (int?)reader["RedirectNodeId"],
                        (string)reader["RedirectUrl"],
                        (int)reader["RedirectHttpCode"],
                        (bool)reader["RedirectPassThroughQueryString"],
                        (bool)reader["ForceRedirect"],
                        (string)reader["Notes"],
                        (bool)reader["Is404"],
                        (string)reader["Referrer"],
                        (DateTime)reader["Inserted"]);
                }
            }
            return null;
        }

        [Obsolete("Remove not found entries also with root id, use other method")]
        public static UrlTrackerModel GetNotFoundEntryByUrl(string url)
        {
            return GetNotFoundEntries().Single(x => x.OldUrl.ToLower() == url.ToLower());
        }

        public static UrlTrackerModel GetNotFoundEntryByRootAndUrl(int redirectRootNodeId, string url)
        {
            return GetNotFoundEntries().Single(x => x.OldUrl.ToLower() == url.ToLower() && x.RedirectRootNodeId == redirectRootNodeId);
        }

        public static List<UrlTrackerModel> GetUrlTrackerEntries(int? maximumRows, int? startRowIndex, string sortExpression = "", bool _404 = false, bool include410Gone = false, bool showAutoEntries = true, bool showCustomEntries = true, bool showRegexEntries = true, string keyword = "", bool onlyForcedRedirects = false)
        {
            List<UrlTrackerModel> urlTrackerEntries = new List<UrlTrackerModel>();
            int intKeyword = 0;

            string query = "SELECT * FROM icUrlTracker WHERE Is404 = @is404 AND RedirectHttpCode != @redirectHttpCode";
            if (onlyForcedRedirects)
                query = string.Concat(query, " AND ForceRedirect = 1");

            if (!string.IsNullOrEmpty(keyword))
            {
                query = string.Concat(query, " AND (OldUrl LIKE @keyword OR OldUrlQueryString LIKE @keyword OR OldRegex LIKE @keyword OR RedirectUrl LIKE @keyword OR Notes LIKE @keyword");
                if (int.TryParse(keyword, out intKeyword))
                    query = string.Concat(query, " OR RedirectNodeId = @intKeyword");
                query = string.Concat(query, ")");
            }

            //todo: check for ce or full sqlserver 
            using(var scope = Current.ScopeProvider.CreateScope())
            {
                var parameters = new {
                    @is404 = _404, 
                    @redirectHttpCode = include410Gone ? 0 : 410 ,
                    @keyword = "%" + keyword + "%",
                    @intKeyword = intKeyword
                };
                var result = scope.Database.Fetch<UrlTrackerModel>(query, parameters);
                urlTrackerEntries.AddRange(result);
            
                urlTrackerEntries = urlTrackerEntries.Where(x => x.RedirectNodeIsPublished).ToList();

                if (!showAutoEntries || !showCustomEntries || !showRegexEntries || !string.IsNullOrEmpty(keyword))
                {
                    IEnumerable<UrlTrackerModel> filteredUrlTrackerEntries = urlTrackerEntries;
                    if (!showAutoEntries)
                        filteredUrlTrackerEntries = filteredUrlTrackerEntries.Where(x => x.ViewType != UrlTrackerViewTypes.Auto);
                    if (!showCustomEntries)
                        filteredUrlTrackerEntries = filteredUrlTrackerEntries.Where(x => x.ViewType != UrlTrackerViewTypes.Custom || (showRegexEntries ? string.IsNullOrEmpty(x.OldUrl) : false));
                    if (!showRegexEntries)
                        filteredUrlTrackerEntries = filteredUrlTrackerEntries.Where(x => !string.IsNullOrEmpty(x.OldUrl));
                    if (!string.IsNullOrEmpty(keyword))
                    {
                        filteredUrlTrackerEntries = filteredUrlTrackerEntries.Where(x =>
                            (x.CalculatedOldUrl != null && x.CalculatedOldUrl.ToLower().Contains(keyword)) ||
                            (x.CalculatedRedirectUrl != null && x.CalculatedRedirectUrl.ToLower().Contains(keyword)) ||
                            (x.OldRegex != null && x.OldRegex.ToLower().Contains(keyword)) ||
                            (x.Notes != null && x.Notes.ToLower().Contains(keyword))
                        );
                    }
                    urlTrackerEntries = filteredUrlTrackerEntries.ToList();
                }

                if (!string.IsNullOrEmpty(sortExpression))
                {
                    string sortBy = sortExpression;
                    bool isDescending = false;

                    if (sortExpression.ToLowerInvariant().EndsWith(" desc"))
                    {
                        sortBy = sortExpression.Substring(0, sortExpression.Length - " desc".Length);
                        isDescending = true;
                    }

                    switch (sortBy)
                    {

                        case "RedirectRootNodeName":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.RedirectRootNodeName) : urlTrackerEntries.OrderBy(x => x.RedirectRootNodeName)).ToList();
                            break;
                        case "CalculatedOldUrl":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.CalculatedOldUrl) : urlTrackerEntries.OrderBy(x => x.CalculatedOldUrl)).ToList();
                            break;
                        case "CalculatedRedirectUrl":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.CalculatedRedirectUrl) : urlTrackerEntries.OrderBy(x => x.CalculatedRedirectUrl)).ToList();
                            break;
                        case "RedirectHttpCode":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.RedirectHttpCode) : urlTrackerEntries.OrderBy(x => x.RedirectHttpCode)).ToList();
                            break;
                        case "Referrer":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.Referrer) : urlTrackerEntries.OrderBy(x => x.Referrer)).ToList();
                            break;
                        case "NotFoundCount":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.NotFoundCount) : urlTrackerEntries.OrderBy(x => x.NotFoundCount)).ToList();
                            break;
                        case "Notes":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.Notes) : urlTrackerEntries.OrderBy(x => x.Notes)).ToList();
                            break;
                        case "Inserted":
                            urlTrackerEntries = (isDescending ? urlTrackerEntries.OrderByDescending(x => x.Inserted) : urlTrackerEntries.OrderBy(x => x.Inserted)).ToList();
                            break;
                    }
                }
                if (startRowIndex.HasValue)
                    urlTrackerEntries = urlTrackerEntries.Skip(startRowIndex.Value).ToList();
                if (maximumRows.HasValue)
                    urlTrackerEntries = urlTrackerEntries.Take(maximumRows.Value).ToList();
            }
            return urlTrackerEntries;
        }

        public static List<UrlTrackerModel> GetNotFoundEntries(int? maximumRows, int? startRowIndex, string sortExpression = "", string keyword = "")
        {
            List<UrlTrackerModel> notFoundEntries = new List<UrlTrackerModel>();
            List<UrlTrackerModel> urlTrackerEntries = GetUrlTrackerEntries(maximumRows, startRowIndex, sortExpression, true, keyword: keyword);
            foreach (var notFoundEntry in urlTrackerEntries.GroupBy(x => x.OldUrl).Select(x => new
            {
                Count = x.Count(),
                UrlTrackerModel = x.First(),
                Referrer = x.Select(y => y.Referrer).Any(y => !string.IsNullOrEmpty(y)) ? x.Select(y => y.Referrer).Where(y => !string.IsNullOrEmpty(y)).GroupBy(y => y).OrderByDescending(y => y.Count()).First().Select(z => z).First() : string.Empty,
                Inserted = x.Select(y => y.Inserted).OrderByDescending(y => y).First()
            }
            ))
            {
                notFoundEntry.UrlTrackerModel.NotFoundCount = notFoundEntry.Count;
                if (!notFoundEntry.Referrer.Contains(UrlTrackerSettings.ReferrerToIgnore))
                    notFoundEntry.UrlTrackerModel.Referrer = notFoundEntry.Referrer;
                notFoundEntry.UrlTrackerModel.Inserted = notFoundEntry.Inserted;
                notFoundEntries.Add(notFoundEntry.UrlTrackerModel);
            }

            if (!string.IsNullOrEmpty(sortExpression))
            {
                string sortBy = sortExpression;
                bool isDescending = false;

                if (sortExpression.ToLowerInvariant().EndsWith(" desc"))
                {
                    sortBy = sortExpression.Substring(0, sortExpression.Length - " desc".Length);
                    isDescending = true;
                }

                switch (sortBy)
                {
                    case "CalculatedOldUrl":
                        notFoundEntries = (isDescending ? notFoundEntries.OrderByDescending(x => x.CalculatedOldUrl) : notFoundEntries.OrderBy(x => x.CalculatedOldUrl)).ToList();
                        break;
                    case "Referrer":
                        notFoundEntries = (isDescending ? notFoundEntries.OrderByDescending(x => x.Referrer) : notFoundEntries.OrderBy(x => x.Referrer)).ToList();
                        break;
                    case "NotFoundCount":
                        notFoundEntries = (isDescending ? notFoundEntries.OrderByDescending(x => x.NotFoundCount) : notFoundEntries.OrderBy(x => x.NotFoundCount)).ToList();
                        break;
                    case "Inserted":
                        notFoundEntries = (isDescending ? notFoundEntries.OrderByDescending(x => x.Inserted) : notFoundEntries.OrderBy(x => x.Inserted)).ToList();
                        break;
                }
            }
            if (startRowIndex.HasValue)
                notFoundEntries = notFoundEntries.Skip(startRowIndex.Value).ToList();
            if (maximumRows.HasValue)
                notFoundEntries = notFoundEntries.Take(maximumRows.Value).ToList();

            return notFoundEntries;
        }

        public static List<UrlTrackerModel> GetUrlTrackerEntries(string sortExpression = "", bool showAutoEntries = true, bool showCustomEntries = true, bool showRegexEntries = true, string keyword = "")
        {
            return GetUrlTrackerEntries(null, null, sortExpression, showAutoEntries: showAutoEntries, showCustomEntries: showCustomEntries, showRegexEntries: showRegexEntries, keyword: keyword);
        }

        public static List<UrlTrackerModel> GetUrlTrackerEntries(string sortExpression)
        {
            return GetUrlTrackerEntries(null, null, sortExpression);
        }

        public static List<UrlTrackerModel> GetNotFoundEntries(string sortExpression, string keyword = "")
        {
            return GetNotFoundEntries(null, null, sortExpression, keyword);
        }

        public static List<UrlTrackerModel> GetNotFoundEntries(string sortExpression)
        {
            return GetNotFoundEntries(null, null, sortExpression);
        }

        public static List<UrlTrackerModel> GetUrlTrackerEntries()
        {
            return GetUrlTrackerEntries(null, null);
        }

        public static List<UrlTrackerModel> GetNotFoundEntries()
        {
            return GetNotFoundEntries(null, null);
        }

        public static bool HasNotFoundEntries()
        {
            string query = "SELECT 1 FROM icUrlTracker WHERE Is404 = 1";
            return ExecuteScalar<int>(query) == 1;
        }
        #endregion

        #region Update
        public static void UpdateUrlTrackerEntry(UrlTrackerModel urlTrackerModel)
        {
            string query = "UPDATE icUrlTracker SET OldUrl = @oldUrl, OldUrlQueryString = @oldUrlQueryString, OldRegex = @oldRegex, RedirectRootNodeId = @redirectRootNodeId, RedirectNodeId = @redirectNodeId, RedirectUrl = @redirectUrl, RedirectHttpCode = @redirectHttpCode, RedirectPassThroughQueryString = @redirectPassThroughQueryString, ForceRedirect = @forceRedirect, Notes = @notes, Is404 = @is404 WHERE Id = @id";

            var sqlParameters = new { oldUrl = urlTrackerModel.OldUrl,
            oldUrlQueryString = urlTrackerModel.OldUrlQueryString,
            oldRegex = urlTrackerModel.OldRegex,
            redirectRootNodeId = urlTrackerModel.RedirectRootNodeId,
            redirectNodeId = urlTrackerModel.RedirectNodeId,
            redirectUrl = urlTrackerModel.RedirectUrl,
            redirectHttpCode = urlTrackerModel.RedirectHttpCode,
            redirectPassThroughQueryString = urlTrackerModel.RedirectPassThroughQueryString,
            forceRedirect = urlTrackerModel.ForceRedirect,
            notes = urlTrackerModel.Notes,
            is404 = urlTrackerModel.Is404,
            id = urlTrackerModel.Id
        };


            ExecuteNonQuery(query, sqlParameters);

            if (urlTrackerModel.ForceRedirect)
                ReloadForcedRedirectsCache();
        }

        public static void Convert410To301(int nodeId)
        {
            string query = "UPDATE icUrlTracker SET RedirectHttpCode = 301 WHERE RedirectHttpCode = 410 AND RedirectNodeId = @redirectNodeId";
            ExecuteNonQuery(query, new {redirectNodeId = nodeId });
        }
        #endregion

        #region Support
        public static bool GetUrlTrackerTableExists()
        {
            string query = "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'icUrlTracker'";
            return ExecuteScalar<int>(query) == 1;
        }

        public static bool GetUrlTrackeOldTableExists()
        {
            string query = "SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @tableName";
            var sqlParameters = new { tableName = UrlTrackerSettings.OldTableName };
            return ExecuteScalar<int>(query, sqlParameters) == 1;
        }

        public static void CreateUrlTrackerTable()
        {
            if (UrlTrackerRepository.GetUrlTrackerTableExists())
                throw new Exception("Table already exists.");

            var folderName = GetFolderName();

            var createTableQuery = EmbeddedResourcesHelper.GetString(string.Concat(folderName, "create-table-1.sql"));
            ExecuteNonQuery(createTableQuery);
        }

        public static void UpdateUrlTrackerTable()
        {
            if (UrlTrackerRepository.GetUrlTrackerTableExists())
            {
                var folderName = GetFolderName();

                for (var i = 1; i <= 3; i++)
                {
                    var alreadyAdded = false;
                    if (DatabaseProvider == Constants.DatabaseProviders.SqlCe)
                    {
                        //Check if columns exists
                        var query =
                            EmbeddedResourcesHelper.GetString(string.Concat(folderName, "check-table-" + i + ".sql"));
                        if (!string.IsNullOrEmpty(query))
                        {
                            var connectionString = Current.ScopeProvider.CreateScope().Database.ConnectionString;   
                            var connection = new SqlConnection(connectionString);
                            SqlCommand command = new SqlCommand(query, connection);
                            // Connection.open() ?
                            var reader = command.ExecuteReader();
                            while (reader.Read())
                            {
                                alreadyAdded = true;
                            }
                        }

                    }

                    if (!alreadyAdded)
                    {
                        var query =
                            EmbeddedResourcesHelper.GetString(string.Concat(folderName, "update-table-" + i + ".sql"));
                        if (!string.IsNullOrEmpty(query))
                            ExecuteNonQuery(query);
                    }
                }
            }
        }

        private static string GetFolderName()
        {
            const string basicFolderName = "InfoCaster.Umbraco.UrlTracker.SQL.";
            var folderName = basicFolderName;
            if (DatabaseProvider == Constants.DatabaseProviders.SqlCe)
            {
                folderName += "SqlServerCompact.";
            }
            else
            {
                folderName += "MicrosoftSqlServer.";
            }
            return folderName;
        }

        public static int MigrateData()
        {
            UmbracoHelper umbracoHelper = Current.Factory.GetInstance<UmbracoHelper>();
            if (!GetUrlTrackerTableExists())
                throw new Exception("Url Tracker table not found.");
            if (!GetUrlTrackeOldTableExists())
                throw new Exception("Old Url Tracker table not found.");

            int newUrlTrackerEntriesCount = 0;
            List<OldUrlTrackerModel> oldUrlTrackerEntries = new List<OldUrlTrackerModel>();
            string query = string.Format("SELECT * FROM {0}", UrlTrackerSettings.OldTableName);

            var connection = new SqlConnection(Current.ScopeProvider.CreateScope().Database.Connection.ConnectionString);
            SqlCommand command = new SqlCommand(query, connection);

            SqlDataReader dataReader = command.ExecuteReader();
            while (dataReader.Read())
            {
                oldUrlTrackerEntries.Add(new OldUrlTrackerModel()
                {
                    NodeId = (int)dataReader["NodeID"],
                    OldUrl = (string)dataReader["OldUrl"],
                    IsCustom = (bool)dataReader["IsCustom"],
                    Message = (string)dataReader["Message"],
                    Inserted = (DateTime)dataReader["Inserted"],
                    IsRegex = (bool)dataReader["IsRegex"]
                });
            }

            foreach (OldUrlTrackerModel oldUrlTrackerEntry in oldUrlTrackerEntries)
            {
                var node = umbracoHelper.Content(oldUrlTrackerEntry.NodeId);
                if ((node.Id > 0 || true) && !string.IsNullOrEmpty(oldUrlTrackerEntry.OldUrl) && oldUrlTrackerEntry.OldUrl != "#")
                {
                    string oldUrl = oldUrlTrackerEntry.OldUrl;
                    Uri oldUri = null;
                    if (!oldUrlTrackerEntry.IsRegex)
                    {
                        if (!oldUrl.StartsWith(Uri.UriSchemeHttp))
                            oldUri = new Uri(_baseUri, oldUrl);
                        else
                            oldUri = new Uri(oldUrl);
                        oldUrl = UrlTrackerHelper.ResolveShortestUrl(oldUri.AbsolutePath);
                    }
                    else
                    {
                        if (oldUrl.StartsWith("^/"))
                            oldUrl = string.Concat("^", oldUrl.Substring(2));
                        if (oldUrl.StartsWith("/"))
                            oldUrl = oldUrl.Substring(1);
                        if (oldUrl.EndsWith("/$"))
                            oldUrl = string.Concat(oldUrl.Substring(0, oldUrl.Length - 2), "$");
                        if (oldUrl.EndsWith("/"))
                            oldUrl = oldUrl.Substring(0, oldUrl.Length - 1);
                    }

                    UrlTrackerModel newUrlTrackerEntry = new UrlTrackerModel(
                        !oldUrlTrackerEntry.IsRegex ? oldUrl : string.Empty,
                        oldUri != null ? !string.IsNullOrEmpty(oldUri.Query) && oldUri.Query.StartsWith("?") ? oldUri.Query.Substring(1) : oldUri.Query : string.Empty,
                        oldUrlTrackerEntry.IsRegex ? oldUrl : string.Empty,
                        node.Root().Id,
                        oldUrlTrackerEntry.NodeId,
                        string.Empty,
                        301,
                        true,
                        false,
                        oldUrlTrackerEntry.Message);
                    newUrlTrackerEntry.Inserted = oldUrlTrackerEntry.Inserted;

                    AddUrlTrackerEntry(newUrlTrackerEntry);

                    newUrlTrackerEntriesCount++;
                }
            }

            return newUrlTrackerEntriesCount;
        }

        public static bool HasInvalidEntries(out List<int> invalidRowIds)
        {
            invalidRowIds = new List<int>();
            bool hasInvalidEntries = false;
            string query = "SELECT Id FROM icUrlTracker WHERE OldUrl IS NULL AND OldRegex IS NULL";
            var connection = new SqlConnection(Current.ScopeProvider.CreateScope().Database.Connection.ConnectionString);
            SqlCommand command = new SqlCommand(query, connection);

            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    hasInvalidEntries = true;
                    invalidRowIds.Add((int)reader["Id"]);
                }
            }
            return hasInvalidEntries;
        }
        #endregion

        #region Forced redirects cache
        public static void ReloadForcedRedirectsCache()
        {
            lock (_cacheLock)
            {
                if (GetUrlTrackerTableExists())
                {
                    _forcedRedirectsCache = GetUrlTrackerEntries(null, null, onlyForcedRedirects: true);
                    LastForcedRedirectCacheRefreshTime = DateTime.UtcNow;
                }
            }
        }

        public static List<UrlTrackerModel> GetForcedRedirects()
        {
            if (_forcedRedirectsCache == null)
            {
                ReloadForcedRedirectsCache();
            }
            else if (UrlTrackerSettings.ForcedRedirectCacheTimeoutEnabled
                     && LastForcedRedirectCacheRefreshTime.AddSeconds(UrlTrackerSettings.ForcedRedirectCacheTimeoutSeconds) < DateTime.UtcNow)
            {
                // Allow continued access to the existing cache when one thread is already reloading it
                if (Monitor.TryEnter(_timeoutCacheLock))
                {
                    try
                    {
                        if (LastForcedRedirectCacheRefreshTime.AddSeconds(UrlTrackerSettings.ForcedRedirectCacheTimeoutSeconds) < DateTime.UtcNow)
                        {
                            ReloadForcedRedirectsCache();
                        }
                    }
                    finally
                    {
                        Monitor.Exit(_timeoutCacheLock);
                    }
                }
            }
            return _forcedRedirectsCache;
        }
        #endregion
    }
}