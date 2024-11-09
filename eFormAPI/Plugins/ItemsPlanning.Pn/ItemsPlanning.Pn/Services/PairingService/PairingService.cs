/*
The MIT License (MIT)

Copyright (c) 2007 - 2021 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using Microsoft.Extensions.Logging;
using Sentry;

namespace ItemsPlanning.Pn.Services.PairingService;

using System;
using Helpers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Models.Pairing;
using ItemsPlanningLocalizationService;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.eFormApi.BasePn.Abstractions;
using Microting.eFormApi.BasePn.Infrastructure.Models.API;
using Microting.eFormApi.BasePn.Infrastructure.Models.Common;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using PlanningService;
using Microting.eForm.Infrastructure.Extensions;
using Infrastructure.Models.Planning;

public class PairingService : IPairingService
{
    private readonly ItemsPlanningPnDbContext _dbContext;
    private readonly IItemsPlanningLocalizationService _itemsPlanningLocalizationService;
    private readonly IEFormCoreService _coreService;
    private readonly IUserService _userService;
    private readonly PairItemWichSiteHelper _pairItemWichSiteHelper;
    private readonly IPlanningService _planningService;
    private readonly ILogger<PairingService> _logger;

    public PairingService(
        ItemsPlanningPnDbContext dbContext,
        IItemsPlanningLocalizationService itemsPlanningLocalizationService,
        IEFormCoreService coreService,
        IUserService userService,
        IPlanningService planningService, ILogger<PairingService> logger)
    {
        _dbContext = dbContext;
        _itemsPlanningLocalizationService = itemsPlanningLocalizationService;
        _coreService = coreService;
        _userService = userService;
        _pairItemWichSiteHelper = new PairItemWichSiteHelper(_dbContext, _coreService);
        _planningService = planningService;
        _logger = logger;
    }
    public async Task<OperationDataResult<PairingsModel>> GetAllPairings(PairingRequestModel pairingRequestModel)
    {
        try
        {
            var sdkCore = await _coreService.GetCore();
            await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
            var sitesQuery = sdkDbContext.Sites
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .AsNoTracking();

            if (pairingRequestModel.SiteIds.Any())
            {
                sitesQuery = sitesQuery.Where(x => pairingRequestModel.SiteIds.Contains(x.Id));
            }
            var deviceUsers = await sitesQuery.Select(x => new CommonDictionaryModel
            {
                Id = x.Id,
                Name = x.Name
            }).ToListAsync();

            var pairingQuery = _dbContext.Plannings
                .Where(x => x.SdkFolderId != null)
                .Where(x => x.SdkFolderId != 0)
                .Where(x => x.IsLocked == false)
                .Where(x => x.IsEditable == true)
                .Where(x => x.IsHidden == false)
                .AsQueryable();

            if (pairingRequestModel.TagIds.Any())
            {
                foreach (var tagId in pairingRequestModel.TagIds)
                {
                    pairingQuery = pairingQuery.Where(x => x.PlanningsTags.Any(y =>
                        y.PlanningTagId == tagId && y.WorkflowState != Constants.WorkflowStates.Removed));
                }
            }
            var language = await _userService.GetCurrentUserLanguage();
            var pairing = await pairingQuery
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => new PairingModel
                {
                    PlanningId = x.Id,
                    PlanningName = x.NameTranslations
                        .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(y => y.LanguageId == language.Id)
                        .Select(y => y.Name)
                        .FirstOrDefault(),
                    PairingValues = x.PlanningSites
                        .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                        .Select(y => new PairingValueModel
                        {
                            DeviceUserId = y.SiteId,
                            Paired = true
                        }).ToList()
                }).ToListAsync();

            // Add users who is not paired
            foreach (var pairingModel in pairing)
            {
                // get status last executed case for planning
                foreach (var pairingModelPairingValue in pairingModel.PairingValues)
                {
                    var planningCaseSite = await _dbContext.PlanningCaseSites
                        .CustomOrderByDescending("UpdatedAt")
                        .Where(z => z.PlanningId == pairingModel.PlanningId)
                        .Where(z => z.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(z => z.MicrotingSdkSiteId == pairingModelPairingValue.DeviceUserId)
                        .FirstOrDefaultAsync();
                    //if (pairingModelPairingValue.DeviceUserId == planningCaseSite.MicrotingSdkSiteId &&
                    //    pairingModelPairingValue.Paired)
                    //{
                    if (planningCaseSite != null)
                    {
                        pairingModelPairingValue.LatestCaseStatus = planningCaseSite.Status;
                        pairingModelPairingValue.PlanningCaseSiteId = planningCaseSite.Id;
                    }
                    //}
                }
                // Add users
                foreach (var deviceUser in deviceUsers)
                {
                    if (deviceUser.Id != null && pairingModel.PairingValues.All(x => x.DeviceUserId != deviceUser.Id))
                    {
                        var pairingValue = new PairingValueModel
                        {
                            DeviceUserId = (int)deviceUser.Id,
                            Paired = false
                        };

                        pairingModel.PairingValues.Add(pairingValue);
                    }
                }

                pairingModel.PairingValues = pairingModel.PairingValues
                    .OrderBy(x => x.DeviceUserId)
                    .ToList();
            }

            // Build result
            var result = new PairingsModel()
            {
                DeviceUsers = deviceUsers.Select(x => new CommonDictionaryModel { Name = x.Name, Id = x.Id}).ToList(),
                Pairings = pairing
            };

            return new OperationDataResult<PairingsModel>(
                true,
                result);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            _logger.LogError(e.Message);
            _logger.LogTrace(e.StackTrace);
            return new OperationDataResult<PairingsModel>(false,
                _itemsPlanningLocalizationService.GetString("ErrorWhileObtainingPlanningPairings"));
        }
    }

    public async Task<OperationResult> PairSingle(PlanningAssignSitesModel requestModel)
    {
        var sdkCore =
            await _coreService.GetCore();
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        try
        {
            var planning = await _dbContext.Plannings
                .Include(x => x.PlanningSites)
                .Where(x => x.Id == requestModel.PlanningId)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .FirstOrDefaultAsync();

            if (planning == null)
            {
                return new OperationResult(false,
                    _itemsPlanningLocalizationService.GetString("PlanningNotFound"));
            }

            // for remove
            var assignmentsRequestIds = requestModel.Assignments
                .Select(x => x.SiteId)
                .ToList();

            var forRemove = planning.PlanningSites
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => !assignmentsRequestIds.Contains(x.SiteId))
                .ToList();

            foreach (var planningSite in forRemove)
            {
                await planningSite.Delete(_dbContext);

                var planningCaseSites = await _dbContext.PlanningCaseSites
                    .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                    .Where(x => x.PlanningId == planningSite.PlanningId)
                    .Where(x => x.MicrotingSdkSiteId == planningSite.SiteId)
                    .ToListAsync();
                foreach (var planningCaseSite in planningCaseSites)
                {
                    var theCase =
                        await sdkDbContext.Cases.SingleOrDefaultAsync(x => x.Id == planningCaseSite.MicrotingSdkCaseId);
                    if (theCase != null)
                    {
                        if (theCase.MicrotingUid != null)
                            await sdkCore.CaseDelete((int) theCase.MicrotingUid);
                    }
                    else
                    {
                        var checkListSite =
                            await sdkDbContext.CheckListSites.SingleOrDefaultAsync(x =>
                                x.Id == planningCaseSite.MicrotingCheckListSitId);
                        if (checkListSite != null)
                        {
                            await sdkCore.CaseDelete(checkListSite.MicrotingUid);
                        }
                    }
                }
            }

            // for create
            var assignmentsIds = planning.PlanningSites
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.SiteId)
                .ToList();

            var assignmentsForCreate = requestModel.Assignments
                .Where(x => !assignmentsIds.Contains(x.SiteId))
                .Select(x => x.SiteId)
                .ToList();

            foreach (var assignmentSiteId in assignmentsForCreate)
            {
                var planningSite = new PlanningSite
                {
                    CreatedByUserId = _userService.UserId,
                    UpdatedByUserId = _userService.UserId,
                    PlanningId = planning.Id,
                    SiteId = assignmentSiteId
                };

                await planningSite.Create(_dbContext);

                var dataResult = await _planningService.Read(planning.Id);
                if(dataResult.Success)
                {
                    await _pairItemWichSiteHelper.Pair(dataResult.Model, assignmentSiteId, planning.RelatedEFormId, planning.Id);
                }
            }

            return new OperationResult(true,
                _itemsPlanningLocalizationService.GetString("PairingUpdatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            _logger.LogError(e.Message);
            _logger.LogTrace(e.StackTrace);
            return new OperationResult(false,
                _itemsPlanningLocalizationService.GetString("ErrorWhileUpdatingItemsPlanning") + $" {e.Message}");
        }
    }

    public async Task<OperationResult> UpdatePairings(List<PairingUpdateModel> updateModels)
    {
        var sdkCore =
            await _coreService.GetCore();
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        try
        {
            var plannings = await _dbContext.Plannings
                .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => new
                {
                    PlanningId = x.Id,
                    Entities = x.PlanningSites
                        .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                        .ToList(),
                    x.RelatedEFormId
                })
                .ToListAsync();

            var pairingModel = updateModels
                .GroupBy(x => x.PlanningId)
                .Select(x => new
                {
                    PlanningId = x.Key,
                    Models = x.ToList()
                }).ToList();

            foreach (var pairing in pairingModel)
            {
                var planning = plannings.FirstOrDefault(x => x.PlanningId == pairing.PlanningId);

                if (planning != null)
                {
                    // for remove
                    var deviceUserIdsForRemove = pairing.Models
                        .Where(x => !x.Paired)
                        .Select(x => x.DeviceUserId)
                        .ToList();

                    var forRemove = planning.Entities
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(x => deviceUserIdsForRemove.Contains(x.SiteId))
                        .ToList();

                    var planningCaseSites = await _dbContext.PlanningCaseSites
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed
                                    && x.WorkflowState != Constants.WorkflowStates.Retracted)
                        .Where(x => deviceUserIdsForRemove.Contains(x.MicrotingSdkSiteId))
                        .Where(x => x.PlanningId == planning.PlanningId)
                        .ToListAsync();

                    foreach (var siteplanningSite in forRemove)
                    {
                        await siteplanningSite.Delete(_dbContext);
                    }

                    foreach (var planningCaseSite in planningCaseSites)
                    {
                        var theCase =
                            await sdkDbContext.Cases.SingleOrDefaultAsync(x => x.Id == planningCaseSite.MicrotingSdkCaseId);
                        if (theCase != null)
                        {
                            if (theCase.MicrotingUid != null)
                                await sdkCore.CaseDelete((int) theCase.MicrotingUid);
                        }
                        else
                        {
                            var checkListSite =
                                await sdkDbContext.CheckListSites.SingleOrDefaultAsync(x =>
                                    x.Id == planningCaseSite.MicrotingCheckListSitId);
                            if (checkListSite != null)
                            {
                                await sdkCore.CaseDelete(checkListSite.MicrotingUid);
                            }
                        }
                    }

                    // for create
                    var sitesForCreateIds = pairing.Models
                        .Where(x => x.Paired)
                        .Select(x => x.DeviceUserId)
                        .ToList();

                    var planningSitesIds = planning.Entities
                        .Select(x => x.SiteId)
                        .ToList();

                    var sitesForCreate = sitesForCreateIds
                        .Where(x => !planningSitesIds.Contains(x))
                        .ToList();

                    foreach (var assignmentSiteId in sitesForCreate)
                    {
                        var newPlanningSite = new PlanningSite
                        {
                            CreatedByUserId = _userService.UserId,
                            UpdatedByUserId = _userService.UserId,
                            PlanningId = pairing.PlanningId,
                            SiteId = assignmentSiteId
                        };
                        await newPlanningSite.Create(_dbContext);

                        var dataResult = await _planningService.Read(planning.PlanningId);
                        if (dataResult.Success)
                        {
                            await _pairItemWichSiteHelper.Pair(dataResult.Model, assignmentSiteId, planning.RelatedEFormId, planning.PlanningId);
                        }
                    }
                }
            }

            return new OperationResult(
                true,
                _itemsPlanningLocalizationService.GetString("PairingsUpdatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            _logger.LogError(e.Message);
            _logger.LogTrace(e.StackTrace);
            return new OperationResult(false,
                _itemsPlanningLocalizationService.GetString("ErrorWhileUpdatingItemsPlanning") + $" {e.Message}");
        }
    }
}