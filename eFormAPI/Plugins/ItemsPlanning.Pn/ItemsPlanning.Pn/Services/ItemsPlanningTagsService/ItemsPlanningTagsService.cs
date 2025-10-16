﻿/*
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

using Sentry;

namespace ItemsPlanning.Pn.Services.ItemsPlanningTagsService;

using ItemsPlanningLocalizationService;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microting.eFormApi.BasePn.Abstractions;
using Microting.eFormApi.BasePn.Infrastructure.Models.API;
using Microting.eFormApi.BasePn.Infrastructure.Models.Common;
using Microting.ItemsPlanningBase.Infrastructure.Data;
using Microting.ItemsPlanningBase.Infrastructure.Data.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Infrastructure.Models.Planning;
using Microting.eForm.Infrastructure.Constants;

public class ItemsPlanningTagsService(
    IItemsPlanningLocalizationService itemsPlanningLocalizationService,
    ILogger<ItemsPlanningTagsService> logger,
    ItemsPlanningPnDbContext dbContext,
    IUserService userService)
    : IItemsPlanningTagsService
{
    public async Task<OperationDataResult<List<PlanningTagModel>>> GetItemsPlanningTags()
    {
        try
        {
            var itemsPlanningTags = await dbContext.PlanningTags
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .AsNoTracking()
                .Select(x => new PlanningTagModel
                {
                    Id = x.Id,
                    Name = x.Name,
                    IsLocked = x.IsLocked
                }).OrderBy(x => x.Name).ToListAsync();

            return new OperationDataResult<List<PlanningTagModel>>(
                true,
                itemsPlanningTags);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationDataResult<List<PlanningTagModel>>(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileObtainingItemsPlanningTags"));
        }
    }

    public async Task<OperationResult> CreateItemsPlanningTag(PlanningTagModel requestModel)
    {
        var currentTag = await dbContext.PlanningTags
            .FirstOrDefaultAsync(x => x.Name == requestModel.Name);

        if (currentTag != null)
        {
            if (currentTag.WorkflowState != Constants.WorkflowStates.Removed)
            {
                return new OperationResult(
                    true,
                    itemsPlanningLocalizationService.GetString("ItemsPlanningTagCreatedSuccessfully"));
            }
            currentTag.WorkflowState = Constants.WorkflowStates.Created;
            currentTag.UpdatedByUserId = userService.UserId;
            await currentTag.Update(dbContext);
            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ItemsPlanningTagCreatedSuccessfully"));
        }

        try
        {
            var itemsPlanningTag = new PlanningTag
            {
                Name = requestModel.Name,
                CreatedByUserId = userService.UserId,
                UpdatedByUserId = userService.UserId
            };

            await itemsPlanningTag.Create(dbContext);

            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ItemsPlanningTagCreatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileCreatingItemsPlanningTag"));
        }
    }

    public async Task<OperationResult> DeleteItemsPlanningTag(int id)
    {
        try
        {
            var itemsPlanningTag = await dbContext.PlanningTags
                .Where(x => x.WorkflowState != Constants.WorkflowStates.Removed)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (itemsPlanningTag == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("ItemsPlanningTagNotFound"));
            }

            var planningsTags = await dbContext.PlanningsTags
                .Where(x => x.PlanningTagId == id).ToListAsync();

            foreach(var planningTag in planningsTags)
            {
                planningTag.UpdatedByUserId = userService.UserId;
                await planningTag.Delete(dbContext);
            }
            itemsPlanningTag.UpdatedByUserId = userService.UserId;
            await itemsPlanningTag.Delete(dbContext);

            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ItemsPlanningTagRemovedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileRemovingItemsPlanningTag"));
        }
    }

    public async Task<OperationResult> UpdateItemsPlanningTag(PlanningTagModel requestModel)
    {
        try
        {
            var itemsPlanningTag = await dbContext.PlanningTags
                .Where(x=> x.WorkflowState != Constants.WorkflowStates.Removed)
                .FirstOrDefaultAsync(x => x.Id == requestModel.Id);

            if (itemsPlanningTag == null)
            {
                return new OperationResult(
                    false,
                    itemsPlanningLocalizationService.GetString("ItemsPlanningTagNotFound"));
            }

            itemsPlanningTag.Name = requestModel.Name;
            itemsPlanningTag.UpdatedByUserId = userService.UserId;

            await itemsPlanningTag.Update(dbContext);

            return new OperationResult(true,
                itemsPlanningLocalizationService.GetString("ItemsPlanningTagUpdatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(false,
                itemsPlanningLocalizationService.GetString("ErrorWhileUpdatingItemsPlanningTag"));
        }
    }

    public async Task<OperationResult> BulkPlanningTags(PlanningBulkTagModel requestModel)
    {
        try
        {
            foreach (var tagName in requestModel.TagNames)
            {
                if (await dbContext.PlanningTags.AnyAsync(x =>
                        x.Name == tagName && x.WorkflowState != Constants.WorkflowStates.Removed))
                {
                    continue; // skip replies
                }

                var itemsPlanningTag = new PlanningTag
                {
                    Name = tagName,
                    CreatedByUserId = userService.UserId,
                    UpdatedByUserId = userService.UserId
                };

                await itemsPlanningTag.Create(dbContext);
            }

            return new OperationResult(
                true,
                itemsPlanningLocalizationService.GetString("ItemsPlanningTagsCreatedSuccessfully"));
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);
            logger.LogError(e.Message);
            logger.LogTrace(e.StackTrace);
            return new OperationResult(
                false,
                itemsPlanningLocalizationService.GetString("ErrorWhileCreatingItemsPlanningTags"));
        }
    }
}