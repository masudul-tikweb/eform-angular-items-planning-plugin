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
using Microting.ItemsPlanningBase.Infrastructure.Enums;
using Sentry;

namespace ItemsPlanning.Pn.Services.PlanningService;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ItemsPlanningLocalizationService;
using Microsoft.EntityFrameworkCore;
using Microting.eForm.Infrastructure.Constants;
using Microting.eFormApi.BasePn.Abstractions;
using Microting.eFormApi.BasePn.Infrastructure.Models.API;
using Microting.eFormApi.BasePn.Infrastructure.Models.Common;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using Infrastructure.Models.Planning;
using Microting.eForm.Infrastructure.Data.Entities;
using Microting.eFormApi.BasePn.Infrastructure.Helpers;

public class PlanningService(
    ItemsPlanningPnDbContext dbContext,
    IItemsPlanningLocalizationService itemsPlanningLocalizationService,
    IUserService userService,
    IEFormCoreService coreService,
    ILogger<PlanningService> logger)
    : IPlanningService
{
    public async Task<OperationDataResult<Paged<PlanningPnModel>>> Index(PlanningsRequestModel pnRequestModel)
    {
        try
        {
            var sdkCore =
                await coreService.GetCore();
            await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();

            var planningsQuery = dbContext.Plannings
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .AsQueryable();

            if (!string.IsNullOrEmpty(pnRequestModel.NameFilter))
            {
                planningsQuery = planningsQuery.Where(x =>
                    x.NameTranslations.Any(y => y.Name.Contains(pnRequestModel.NameFilter)));
            }

            if (!string.IsNullOrEmpty(pnRequestModel.DescriptionFilter))
            {
                planningsQuery = planningsQuery.Where(x =>
                    x.Description.Contains(pnRequestModel.DescriptionFilter));
            }

            var excludeSort = new List<string>{ "TranslatedName", "SdkFolderName" };
            // sort
            planningsQuery = QueryHelper.AddSortToQuery(planningsQuery, pnRequestModel.Sort,
                pnRequestModel.IsSortDsc, excludeSort);

            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var tagId in pnRequestModel.TagIds)
            {
                planningsQuery = planningsQuery.Where(x => x.PlanningsTags.Any(y =>
                    y.PlanningTagId == tagId && y.WorkflowState != Constants.WorkflowStates.Removed));
            }

            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var deviceUserId in pnRequestModel.DeviceUserIds)
            {
                planningsQuery = planningsQuery.Where(x => x.PlanningSites
                    .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                    .Select(y => y.SiteId)
                    .Contains(deviceUserId));
            }

            // calculate total before pagination
            var total = await planningsQuery.Select(x => x.Id).CountAsync();

            // add select
            var localeString = await userService.GetCurrentUserLocale();
            if (string.IsNullOrEmpty(localeString))
            {
                return new OperationDataResult<Paged<PlanningPnModel>>(false,
                    itemsPlanningLocalizationService.GetString("LocaleDoesNotExist"));
            }


            var language = sdkDbContext.Languages.First(x => x.LanguageCode == localeString);
            var folderIds = await planningsQuery
                .Where(x => x.SdkFolderId.HasValue)
                .Select(x => x.SdkFolderId.Value)
                .ToListAsync();
            var foldersWithNames = await sdkDbContext.FolderTranslations
                .Include(x => x.Folder)
                .ThenInclude(x => x.Parent)
                .ThenInclude(x => x.FolderTranslations)
                .Where(x => folderIds.Contains(x.FolderId))
                .Where(x => x.LanguageId == language.Id)
                .Select(x => new CommonDictionaryModel
                {
                    Id = x.FolderId,
                    Name = x.Name,
                    Description = x.Folder.ParentId.HasValue ?
                        x.Folder.Parent.FolderTranslations
                            .Where(y => y.LanguageId == language.Id)
                            .Select(y => y.Name)
                            .FirstOrDefault() :
                        "",
                })
                .ToListAsync();

            var planningQueryWithSelect = AddSelectToPlanningQuery(planningsQuery, language);

            planningQueryWithSelect
                = planningQueryWithSelect
                    .Skip(pnRequestModel.Offset)
                    .Take(pnRequestModel.PageSize);

            var checkListIds = await planningsQuery.Select(x => x.RelatedEFormId).ToListAsync();
            var checkListWorkflowState = sdkDbContext.CheckLists.Where(x => checkListIds.Contains(x.Id))
                .Select(checkList => new KeyValuePair<int, string>(checkList.Id, checkList.WorkflowState))
                .ToList();

            // add select and take objects from db
            //var sql = planningQueryWithSelect.ToQueryString();
            var plannings = await planningQueryWithSelect.ToListAsync();

            // get site names
            var assignedSitesFromPlanning = plannings.SelectMany(y => y.AssignedSites).ToList();

            var sites = await sdkDbContext.Sites
                .AsNoTracking()
                //.Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(x => assignedSitesFromPlanning.Select(y => y.SiteId).Contains(x.Id))
                .Select(x => new CommonDictionaryModel
                {
                    Id = x.Id,
                    Name = x.Name
                }).ToListAsync();

            foreach (var planning in plannings)
            {
                foreach (var assignedSite in assignedSitesFromPlanning)
                {
                    var site = sites.First(site => site.Id == assignedSite.SiteId);
                    assignedSite.Name = site.Name;
                    assignedSite.Status = dbContext.PlanningCaseSites
                        .Where(x => x.PlanningId == planning.Id
                                    && x.MicrotingSdkSiteId == site.Id
                                    && x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Select(x => x.Status).FirstOrDefault();
                }

                var (_, value) = checkListWorkflowState.SingleOrDefault(x => x.Key == planning.BoundEform.RelatedEFormId);
                planning.BoundEform.IsEformRemoved = value == Constants.WorkflowStates.Removed;

                // This is done to update existing Plannings to using EFormSdkFolderId instead of EFormSdkFolderName
                if (planning.Folder.EFormSdkFolderId is 0 or null)
                {
                    var locateFolder = await sdkDbContext.Folders
                        .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Where(x => x.Name == planning.Folder.EFormSdkFolderName)
                        .FirstOrDefaultAsync();

                    if (locateFolder != null)
                    {
                        var thePlanning = await dbContext.Plannings.SingleAsync(x => x.Id == planning.Id);
                        thePlanning.SdkFolderId = locateFolder.Id;
                        await thePlanning.Update(dbContext);
                        planning.Folder.EFormSdkFolderId = locateFolder.Id;
                    }
                }

                // fill folder names
                planning.Folder.EFormSdkFolderName = planning.Folder.EFormSdkFolderId.HasValue
                    ? foldersWithNames
                        .Where(y => y.Id == planning.Folder.EFormSdkFolderId.Value)
                        .Select(y => y.Name)
                        .FirstOrDefault()
                    : "";
                planning.Folder.EFormSdkParentFolderName = planning.Folder.EFormSdkFolderId.HasValue
                    ? foldersWithNames
                        .Where(y => y.Id == planning.Folder.EFormSdkFolderId.Value)
                        .Select(y => y.Description)
                        .FirstOrDefault()
                    : "";
            }

            // sorting after select. some field can't sort before select
            if (excludeSort.Contains(pnRequestModel.Sort))
            {
                plannings = (pnRequestModel.Sort == "SdkFolderName"
                    ? pnRequestModel.IsSortDsc
                        ? plannings.AsQueryable().OrderByDescending(x => x.Folder.EFormSdkFolderName)
                        : plannings.AsQueryable().OrderBy(x => x.Folder.EFormSdkFolderName)
                    : QueryHelper.AddSortToQuery(plannings.AsQueryable(), pnRequestModel.Sort,
                        pnRequestModel.IsSortDsc)).ToList();
            }

            var planningsModel = new Paged<PlanningPnModel>
            {
                Total = total,
                Entities = plannings
            };

            return new OperationDataResult<Paged<PlanningPnModel>>(true, planningsModel);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationDataResult<Paged<PlanningPnModel>>(false,
                itemsPlanningLocalizationService.GetString("ErrorObtainingLists"));
        }
    }

    public async Task<OperationResult> Create(PlanningCreateModel model)
    {
        var sdkCore =
            await coreService.GetCore();
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        try
        {
            var tagIds = new List<int>();

            tagIds.AddRange(model.TagsIds);

            var localeString = await userService.GetCurrentUserLocale();
            if (string.IsNullOrEmpty(localeString))
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("LocaleDoesNotExist"));
            }
            var language = sdkDbContext.Languages.Single(x => x.LanguageCode == localeString);
            if (model.BoundEform == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("InfoAboutEformIsNull"));
            }
            var template = await coreService.GetCore().Result.TemplateItemRead(model.BoundEform.RelatedEFormId, language);
            if (template == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("EformNotFound"));
            }
            if (model.Folder == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("InfoAboutFolderIsNull"));
            }
            var sdkFolder = await sdkDbContext.Folders
                .Include(x => x.Parent)
                .FirstOrDefaultAsync(x => x.Id == model.Folder.EFormSdkFolderId);

            if (sdkFolder == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("FolderNotFound"));
            }

            var planning = new Planning
            {
                Description = model.Description,
                BuildYear = model.BuildYear,
                Type = model.Type,
                LocationCode = model.LocationCode,
                CreatedByUserId = userService.UserId,
                CreatedAt = DateTime.UtcNow,
                IsLocked = false,
                IsEditable = true,
                IsHidden = false,
                RepeatEvery = model.Reiteration.RepeatEvery,
                RepeatUntil = model.Reiteration.RepeatUntil,
                RepeatType = model.Reiteration.RepeatType,
                DayOfWeek = model.Reiteration.DayOfWeek,
                DayOfMonth = model.Reiteration.DayOfMonth,
                Enabled = true,
                RelatedEFormId = model.BoundEform.RelatedEFormId,
                RelatedEFormName = template.Label,
                SdkFolderName = sdkFolder.Name,
                SdkFolderId = model.Folder.EFormSdkFolderId,
                PlanningsTags = new List<PlanningsTags>(),
                DaysBeforeRedeploymentPushMessageRepeat = model.Reiteration.PushMessageEnabled,
                DaysBeforeRedeploymentPushMessage = model.Reiteration.DaysBeforeRedeploymentPushMessage,
                PushMessageOnDeployment = model.Reiteration.PushMessageOnDeployment,
                StartDate = model.Reiteration.StartDate ?? DateTime.UtcNow,
                PlanningNumber = model.PlanningNumber
            };


            foreach (var tagId in tagIds)
            {
                planning.PlanningsTags.Add(
                    new PlanningsTags
                    {
                        CreatedByUserId = userService.UserId,
                        UpdatedByUserId = userService.UserId,
                        PlanningTagId = tagId
                    });
            }

            await planning.Create(dbContext);
            var languages = await sdkDbContext.Languages.Where(x => x.IsActive == true).ToListAsync();
            foreach (var translation in model.TranslationsName)
            {
                var languageId = languages.Where(x => x.Name == translation.Language || x.LanguageCode == translation.LocaleName)
                    .Select(x => x.Id)
                    .FirstOrDefault();
                if (languageId == default)
                {
                    return new OperationResult(
                        true,
                        itemsPlanningLocalizationService.GetString("LocaleDoesNotExist"));
                }

                var planningNameTranslations = new PlanningNameTranslation()
                {
                    LanguageId = languageId,
                    PlanningId = planning.Id,
                    Name = translation.Name,
                    CreatedByUserId = userService.UserId,
                    UpdatedByUserId = userService.UserId
                };
                await planningNameTranslations.Create(dbContext);
            }

            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ListCreatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(false,
                itemsPlanningLocalizationService.GetString("ErrorWhileCreatingList"));
        }
    }

    public async Task<OperationDataResult<PlanningPnModel>> Read(int planningId)
    {
        try
        {
            var sdkCore =
                await coreService.GetCore();
            await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
            var localeString = await userService.GetCurrentUserLocale();
            if (string.IsNullOrEmpty(localeString))
            {
                return new OperationDataResult<PlanningPnModel>(
                    false,
                    itemsPlanningLocalizationService.GetString("LocaleDoesNotExist"));
            }
            var language = sdkDbContext.Languages.Single(x => x.LanguageCode == localeString);
            var planningQuery = dbContext.Plannings
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed && x.Id == planningId);

            var folderId = await planningQuery
                .Where(x => x.SdkFolderId.HasValue)
                .Select(x => x.SdkFolderId.Value)
                .SingleAsync();
            var foldersWithNames = await sdkDbContext.FolderTranslations
                .Include(x => x.Folder)
                .ThenInclude(x => x.Parent)
                .ThenInclude(x => x.FolderTranslations)
                .Where(x => folderId == x.FolderId)
                .Where(x => x.LanguageId == language.Id)
                .Select(x => new CommonDictionaryModel
                {
                    Id = x.FolderId,
                    Name = x.Name,
                    Description = x.Folder.ParentId.HasValue ?
                        x.Folder.Parent.FolderTranslations
                            .Where(y => y.LanguageId == language.Id)
                            .Select(y => y.Name)
                            .FirstOrDefault() :
                        "",
                })
                .ToListAsync();

            var planning = await AddSelectToPlanningQuery(planningQuery, language).FirstOrDefaultAsync();

            if (planning == null)
            {
                return new OperationDataResult<PlanningPnModel>(
                    false,
                    itemsPlanningLocalizationService.GetString("ListNotFound"));
            }

            planning.Folder.EFormSdkFolderName = planning.Folder.EFormSdkFolderId.HasValue
                ? foldersWithNames
                    .Where(y => y.Id == planning.Folder.EFormSdkFolderId.Value)
                    .Select(y => y.Name)
                    .FirstOrDefault()
                : "";
            planning.Folder.EFormSdkParentFolderName = planning.Folder.EFormSdkFolderId.HasValue
                ? foldersWithNames
                    .Where(y => y.Id == planning.Folder.EFormSdkFolderId.Value)
                    .Select(y => y.Description)
                    .FirstOrDefault()
                : "";

            // get site names
            var sites = await sdkDbContext.Sites
                .AsNoTracking()
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => new CommonDictionaryModel
                {
                    Id = x.Id,
                    Name = x.Name
                }).ToListAsync();

            foreach (var assignedSite in planning.AssignedSites)
            {
                foreach (var site in sites.Where(site => site.Id == assignedSite.SiteId))
                {
                    assignedSite.Name = site.Name;
                    assignedSite.Status = dbContext.PlanningCaseSites
                        .Where(x => x.PlanningId == planning.Id
                                    && x.MicrotingSdkSiteId == site.Id
                                    && x.WorkflowState != Constants.WorkflowStates.Removed)
                        .Select(x => x.Status).FirstOrDefault();
                }
            }

            planning.AssignedSites = planning.AssignedSites
                .OrderBy(x => x.Name)
                .ToList();

            return new OperationDataResult<PlanningPnModel>(
                true,
                planning);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationDataResult<PlanningPnModel>(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileObtainingList"));
        }
    }

    public async Task<OperationResult> Update(PlanningUpdateModel updateModel)
    {
        // await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        var sdkCore =
            await coreService.GetCore();
        await using var sdkDbContext = sdkCore.DbContextHelper.GetDbContext();
        try
        {
            var localeString = await userService.GetCurrentUserLocale();
            if (string.IsNullOrEmpty(localeString))
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("LocaleDoesNotExist"));
            }
            var language = sdkDbContext.Languages.Single(x => x.LanguageCode == localeString);

            if (updateModel.BoundEform == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("InfoAboutEformIsNull"));
            }

            var template = await sdkCore.TemplateItemRead(updateModel.BoundEform.RelatedEFormId, language);

            if (template == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("EformNotFound"));
            }

            if (updateModel.Folder == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("InfoAboutFolderIsNull"));
            }

            var sdkFolder = await sdkDbContext.Folders
                .Include(x => x.Parent)
                .FirstOrDefaultAsync(x => x.Id == updateModel.Folder.EFormSdkFolderId);

            if (sdkFolder == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("FolderNotFound"));
            }

            var planning = await dbContext.Plannings
                .Include(x => x.PlanningsTags)
                .FirstOrDefaultAsync(x => x.Id == updateModel.Id);

            if (planning == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("PlanningNotFound"));
            }

            var translationsPlanning = dbContext.PlanningNameTranslation
                .Where(x => x.Planning.Id == planning.Id)
                .ToList();
            foreach (var translation in updateModel.TranslationsName)
            {
                var updateTranslation = translationsPlanning
                    .FirstOrDefault(x => x.Id == translation.Id);
                if (updateTranslation != null)
                {
                    updateTranslation.Name = translation.Name;
                    await updateTranslation.Update(dbContext);
                }
            }

            planning.DaysBeforeRedeploymentPushMessage = updateModel.Reiteration.DaysBeforeRedeploymentPushMessage;
            planning.DaysBeforeRedeploymentPushMessageRepeat = updateModel.Reiteration.PushMessageEnabled;
            planning.PushMessageOnDeployment = updateModel.Reiteration.PushMessageOnDeployment;
            planning.DoneByUserNameEnabled = updateModel.EnabledFields.DoneByUserNameEnabled;
            planning.NumberOfImagesEnabled = updateModel.EnabledFields.NumberOfImagesEnabled;
            planning.PlanningNumberEnabled = updateModel.EnabledFields.PlanningNumberEnabled;
            planning.UploadedDataEnabled = updateModel.EnabledFields.UploadedDataEnabled;
            planning.LocationCodeEnabled = updateModel.EnabledFields.LocationCodeEnabled;
            planning.DescriptionEnabled = updateModel.EnabledFields.DescriptionEnabled;
            planning.StartDate = updateModel.Reiteration.StartDate ?? DateTime.UtcNow;
            planning.DeployedAtEnabled = updateModel.EnabledFields.DeployedAtEnabled;
            planning.BuildYearEnabled = updateModel.EnabledFields.BuildYearEnabled;
            planning.DoneAtEnabled = updateModel.EnabledFields.DoneAtEnabled;
            planning.RelatedEFormId = updateModel.BoundEform.RelatedEFormId;
            planning.LabelEnabled = updateModel.EnabledFields.LabelEnabled;
            planning.TypeEnabled = updateModel.EnabledFields.TypeEnabled;
            planning.RepeatUntil = updateModel.Reiteration.RepeatUntil;
            planning.RepeatEvery = updateModel.Reiteration.RepeatEvery;
            planning.SdkFolderId = updateModel.Folder.EFormSdkFolderId;
            planning.RepeatType = updateModel.Reiteration.RepeatType;
            planning.DayOfMonth = updateModel.Reiteration.DayOfMonth;
            planning.DayOfWeek = updateModel.Reiteration.DayOfWeek;
            planning.PlanningNumber = updateModel.PlanningNumber;
            planning.LocationCode = updateModel.LocationCode;
            planning.Description = updateModel.Description;
            planning.UpdatedByUserId = userService.UserId;
            planning.BuildYear = updateModel.BuildYear;
            planning.RelatedEFormName = template.Label;
            planning.SdkFolderName = sdkFolder.Name;
            planning.UpdatedAt = DateTime.UtcNow;
            planning.Type = updateModel.Type;

            var tagIds = planning.PlanningsTags
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(x => x.PlanningTagId)
                .ToList();

            var tagsForDelete = planning.PlanningsTags
                .Where(x => !updateModel.TagsIds.Contains(x.PlanningTagId))
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .ToList();

            var tagsForCreate = updateModel.TagsIds
                .Where(x => !tagIds.Contains(x))
                .ToList();

            foreach (var tag in tagsForDelete)
            {
                dbContext.PlanningsTags.Remove(tag);
            }

            foreach (var tagId in tagsForCreate)
            {
                var planningsTags = new PlanningsTags
                {
                    CreatedByUserId = userService.UserId,
                    UpdatedByUserId = userService.UserId,
                    PlanningId = planning.Id,
                    PlanningTagId = tagId
                };

                await dbContext.PlanningsTags.AddAsync(planningsTags);
            }

            await planning.Update(dbContext);

            // transaction.Commit();
            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ListUpdatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileUpdatingList"));
        }
    }

    public async Task<OperationResult> Delete(int id)
    {
        try
        {
            var planning = await dbContext.Plannings
                .SingleAsync(x => x.Id == id);

            if (planning == null)
            {
                return new OperationResult(false,
                    itemsPlanningLocalizationService.GetString("PlanningNotFound"));
            }

            await DeleteOnePlanning(planning);

            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ListDeletedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileRemovingList"));
        }

    }

    private static IQueryable<PlanningPnModel> AddSelectToPlanningQuery(IQueryable<Planning> planningQueryable, Language languageIemPlanning)
    {
        return planningQueryable.Select(x => new PlanningPnModel
        {
            Id = x.Id,
            Description = x.Description,
            BuildYear = x.BuildYear,
            Type = x.Type,
            LocationCode = x.LocationCode,
            PlanningNumber = x.PlanningNumber,
            LastExecutedTime = x.LastExecutedTime,
            NextExecutionTime = x.NextExecutionTime,
            PushMessageSent = x.PushMessageSent,
            IsLocked = x.IsLocked,
            IsEditable = x.IsEditable,
            IsHidden = x.IsHidden,
            CreatedAt = x.CreatedAt,
            UpdatedAt = x.UpdatedAt,
            TranslationsName = x.NameTranslations.Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(y => new PlanningNameTranslations
                {
                    Id = y.Id,
                    LanguageId = y.LanguageId,
                    Name = y.Name
                }).ToList(),
            TranslatedName = x.NameTranslations
                .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                .Where(y => y.LanguageId == languageIemPlanning.Id)
                .Select(y => y.Name)
                .FirstOrDefault(),
            Reiteration = new PlanningReiterationModel
            {
                RepeatEvery = x.RepeatEvery,
                RepeatType = x.RepeatType,
                RepeatUntil = x.RepeatUntil,
                DayOfWeek = x.RepeatType == RepeatType.Day ? null : x.DayOfWeek,
                DayOfMonth = x.RepeatType == RepeatType.Day ? null : x.RepeatType == RepeatType.Week ? null : (int)x.DayOfMonth,
                StartDate = x.StartDate,
                PushMessageEnabled = x.DaysBeforeRedeploymentPushMessageRepeat,
                DaysBeforeRedeploymentPushMessage = x.DaysBeforeRedeploymentPushMessage,
                PushMessageOnDeployment = x.PushMessageOnDeployment
            },
            BoundEform = new PlanningEformModel
            {
                RelatedEFormId = x.RelatedEFormId,
                RelatedEFormName = x.RelatedEFormName
            },
            Folder = new PlanningFolderModel
            {
                EFormSdkFolderName = x.SdkFolderName, // fill it only for "This is done to update existing Plannings to using EFormSdkFolderId instead of EFormSdkFolderName", and refill it's field after
                EFormSdkFolderId = x.SdkFolderId
            },
            AssignedSites = x.PlanningSites
                .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(y => new PlanningAssignedSitesModel
                {
                    Id = y.Id,
                    SiteId = y.SiteId
                }).ToList(),
            Tags = x.PlanningsTags
                .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(y => new CommonDictionaryModel
                {
                    Id = y.PlanningTagId,
                    Name = y.PlanningTag.Name
                }).ToList(),
            TagsIds = x.PlanningsTags
                .Where(y => y.WorkflowState != Constants.WorkflowStates.Removed)
                .Select(y => y.PlanningTagId).ToList(),
            EnabledFields = new PlanningFieldsModel
            {
                PlanningNumberEnabled = x.PlanningNumberEnabled,
                BuildYearEnabled = x.BuildYearEnabled,
                DeployedAtEnabled = x.DeployedAtEnabled,
                DescriptionEnabled = x.DescriptionEnabled,
                DoneAtEnabled = x.DoneAtEnabled,
                DoneByUserNameEnabled = x.DoneByUserNameEnabled,
                LabelEnabled = x.LabelEnabled,
                LocationCodeEnabled = x.LocationCodeEnabled,
                NumberOfImagesEnabled = x.NumberOfImagesEnabled,
                TypeEnabled = x.TypeEnabled,
                UploadedDataEnabled = x.UploadedDataEnabled
            }
        });
    }

    public async Task<OperationResult> MultipleDeletePlannings(List<int> planningIds)
    {
        foreach (var planningId in planningIds)
        {
            try
            {
                var planning = await dbContext.Plannings
                    .SingleAsync(x => x.Id == planningId);

                if (planning == null)
                {
                    return new OperationResult(false,
                        itemsPlanningLocalizationService.GetString("PlanningNotFound"));
                }

                await DeleteOnePlanning(planning);
            }
            catch (Exception e)
            {
                Trace.TraceError(e.Message);
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("ErrorWhileRemovingList"));
            }
        }
        return new OperationResult(
            true,
            itemsPlanningLocalizationService.GetString("ListDeletedSuccessfully"));
    }

    private async Task DeleteOnePlanning(Planning planning)
    {
        var core = await coreService.GetCore();
        await using var sdkDbContext = core.DbContextHelper.GetDbContext();
        var planningCases = await dbContext.PlanningCases
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .Where(x => x.PlanningId == planning.Id)
            .ToListAsync();

        foreach (var planningCase in planningCases)
        {
            var planningCaseSites = await dbContext.PlanningCaseSites
                .Where(x => x.PlanningCaseId == planningCase.Id).ToListAsync();
            foreach (var planningCaseSite in planningCaseSites
                         .Where(planningCaseSite => planningCaseSite.MicrotingSdkCaseId != 0)
                         .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed))
            {
                var theCase =
                    await sdkDbContext.Cases.SingleOrDefaultAsync(x => x.Id == planningCaseSite.MicrotingSdkCaseId);
                if (theCase != null)
                {
                    if (theCase.MicrotingUid != null)
                        await core.CaseDelete((int) theCase.MicrotingUid);
                }
                else
                {
                    var checkListSite =
                        await sdkDbContext.CheckListSites.SingleOrDefaultAsync(x =>
                            x.Id == planningCaseSite.MicrotingCheckListSitId);
                    if (checkListSite != null)
                    {
                        await core.CaseDelete(checkListSite.MicrotingUid);
                    }
                }
            }
            // Delete planning case
            await planningCase.Delete(dbContext);
        }

        var planningSites = await dbContext.PlanningSites
            .Where(x => x.PlanningId == planning.Id)
            .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
            .ToListAsync();
        foreach (var planningSite in planningSites)
        {
            await planningSite.Delete(dbContext);
        }

        var nameTranslationsPlanning =
            await dbContext.PlanningNameTranslation
                .Where(x => x.Planning.Id == planning.Id)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .ToListAsync();

        foreach (var translation in nameTranslationsPlanning)
        {
            await translation.Delete(dbContext);
        }

        // Delete planning
        await planning.Delete(dbContext);
    }

    public async Task<OperationResult> DeletePlanningCase(int planningCaseId)
    {
        try
        {
            var planningCase = await dbContext.PlanningCases
                .Where(x => x.Id == planningCaseId)
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .FirstOrDefaultAsync();
            if (planningCase == null)
            {
                return new OperationResult(false, itemsPlanningLocalizationService.GetString("CaseNotFound"));
            }

            var core = await coreService.GetCore();
            await core.CaseDeleteResult(planningCase.MicrotingSdkCaseId);

            await planningCase.Delete(dbContext);
            return new OperationResult(true, itemsPlanningLocalizationService.GetString("CaseDeleteSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileDeleteCase"));
        }
    }
}