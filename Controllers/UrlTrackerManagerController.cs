﻿using System;
using InfoCaster.Umbraco.UrlTracker.Models;
using InfoCaster.Umbraco.UrlTracker.ViewModels;
using System.Linq;
using System.Web.Http;
using InfoCaster.Umbraco.UrlTracker.NewRepositories;
using InfoCaster.Umbraco.UrlTracker.Services;
using Umbraco.Web.WebApi;

namespace InfoCaster.Umbraco.UrlTracker.Controllers
{
	public class UrlTrackerManagerController : UmbracoAuthorizedApiController
	{
		private readonly IUrlTrackerService _urlTrackerService;

		public UrlTrackerManagerController(IUrlTrackerService urlTrackerService)
		{
			_urlTrackerService = urlTrackerService;
		}

		#region General

		[HttpPost]
		public IHttpActionResult UpdateEntry(UrlTrackerModel model)
		{
			_urlTrackerService.UpdateEntry(model);
			return Ok();
		}


		[HttpPost]
		public IHttpActionResult DeleteEntry(int id, bool is404 = false)
		{
			_urlTrackerService.DeleteEntryById(id, is404);
			return Ok();
		}

		#endregion

		#region Redirects

		[HttpPost]
		public IHttpActionResult AddRedirect([FromBody] UrlTrackerModel model)
		{
			_urlTrackerService.AddRedirect(model);
			return Ok();
		}

		[HttpGet]
		public IHttpActionResult GetRedirects(int skip, int amount)
		{
			var entriesResult = _urlTrackerService.GetRedirects(skip, amount);

			var model = new UrlTrackerOverviewModel
			{
				Entries = entriesResult.Records,
				NumberOfEntries = entriesResult.TotalRecords
			};

			return Ok(model);
		}

		[HttpGet]
		public IHttpActionResult GetRedirectsByFilter(int skip, int amount, string query, UrlTrackerSortType sortType)
		{
			var entriesResult = _urlTrackerService.GetRedirectsByFilter(skip, amount, sortType, query);

			var model = new UrlTrackerOverviewModel
			{
				Entries = entriesResult.Records,
				NumberOfEntries = entriesResult.TotalRecords
			};

			return Ok(model);
		}

		#endregion

		#region Not founds

		[HttpGet]
		public IHttpActionResult GetNotFounds(int skip, int amount)
		{
			var entriesResult = _urlTrackerService.GetNotFounds(skip, amount);

			var model = new UrlTrackerOverviewModel
			{
				Entries = entriesResult.Records,
				NumberOfEntries = entriesResult.TotalRecords
			};

			return Ok(model);
		}

		[HttpGet]
		public IHttpActionResult GetNotFoundsByFilter(int skip, int amount, string query, UrlTrackerSortType sortType)
		{
			var entriesResult = _urlTrackerService.GetNotFoundsByFilter(skip, amount, sortType, query);

			var model = new UrlTrackerOverviewModel
			{
				Entries = entriesResult.Records,
				NumberOfEntries = entriesResult.TotalRecords
			};

			return Ok(model);
		}

		#endregion
	}
}