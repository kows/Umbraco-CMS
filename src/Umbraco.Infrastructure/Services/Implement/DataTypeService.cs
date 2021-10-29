using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Exceptions;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Serialization;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Persistence.Repositories.Implement;
using Umbraco.Extensions;

namespace Umbraco.Cms.Core.Services.Implement
{
    /// <summary>
    /// Represents the DataType Service, which is an easy access to operations involving <see cref="IDataType"/>
    /// </summary>
    public class DataTypeService : RepositoryService, IDataTypeService
    {
        private readonly IDataValueEditorFactory _dataValueEditorFactory;
        private readonly IDataTypeRepository _dataTypeRepository;
        private readonly IDataTypeContainerRepository _dataTypeContainerRepository;
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IAuditRepository _auditRepository;
        private readonly IEntityRepository _entityRepository;
        private readonly IIOHelper _ioHelper;
        private readonly ILocalizedTextService _localizedTextService;
        private readonly ILocalizationService _localizationService;
        private readonly IShortStringHelper _shortStringHelper;
        private readonly IJsonSerializer _jsonSerializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="DataTypeService"/> class.
        /// </summary>
        public DataTypeService(
            IDataValueEditorFactory dataValueEditorFactory,
            IScopeProvider provider, ILoggerFactory loggerFactory, IEventMessagesFactory eventMessagesFactory,
            IDataTypeRepository dataTypeRepository, IDataTypeContainerRepository dataTypeContainerRepository,
            IAuditRepository auditRepository, IEntityRepository entityRepository, IContentTypeRepository contentTypeRepository,
            IIOHelper ioHelper, ILocalizedTextService localizedTextService, ILocalizationService localizationService,
            IShortStringHelper shortStringHelper,
            IJsonSerializer jsonSerializer)
            : base(provider, loggerFactory, eventMessagesFactory)
        {
            _dataValueEditorFactory = dataValueEditorFactory;
            _dataTypeRepository = dataTypeRepository;
            _dataTypeContainerRepository = dataTypeContainerRepository;
            _auditRepository = auditRepository;
            _entityRepository = entityRepository;
            _contentTypeRepository = contentTypeRepository;
            _ioHelper = ioHelper;
            _localizedTextService = localizedTextService;
            _localizationService = localizationService;
            _shortStringHelper = shortStringHelper;
            _jsonSerializer = jsonSerializer;
        }

        #region Containers

        /// <inheritdoc/>
        public Attempt<OperationResult<OperationResultType, EntityContainer>> CreateContainer(int parentId, Guid key, string name, int userId = Cms.Core.Constants.Security.SuperUserId)
        {
            EventMessages eventMessages = EventMessagesFactory.Get();
            using (IScope scope = ScopeProvider.CreateScope())
            {
                try
                {
                    var container = new EntityContainer(Cms.Core.Constants.ObjectTypes.DataType)
                    {
                        Name = name,
                        ParentId = parentId,
                        CreatorId = userId,
                        Key = key
                    };

                    var savingEntityContainerNotification = new EntityContainerSavingNotification(container, eventMessages);
                    if (scope.Notifications.PublishCancelable(savingEntityContainerNotification))
                    {
                        scope.Complete();
                        return OperationResult.Attempt.Cancel(eventMessages, container);
                    }

                    _dataTypeContainerRepository.Save(container);
                    scope.Complete();

                    scope.Notifications.Publish(new EntityContainerSavedNotification(container, eventMessages).WithStateFrom(savingEntityContainerNotification));

                    // TODO: Audit trail ?
                    return OperationResult.Attempt.Succeed(eventMessages, container);
                }
                catch (Exception ex)
                {
                    return OperationResult.Attempt.Fail<EntityContainer>(eventMessages, ex);
                }
            }
        }

        /// <inheritdoc/>
        public EntityContainer GetContainer(int containerId)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                return _dataTypeContainerRepository.Get(containerId);
            }
        }

        /// <inheritdoc/>
        public EntityContainer GetContainer(Guid containerId)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                return ((EntityContainerRepository) _dataTypeContainerRepository).Get(containerId);
            }
        }

        /// <inheritdoc/>
        public IEnumerable<EntityContainer> GetContainers(string name, int level)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                return ((EntityContainerRepository) _dataTypeContainerRepository).Get(name, level);
            }
        }

        /// <inheritdoc/>
        public IEnumerable<EntityContainer> GetContainers(IDataType dataType)
        {
            var ancestorIds = dataType.Path.Split(Constants.CharArrays.Comma, StringSplitOptions.RemoveEmptyEntries)
                .Select(x =>
                {
                    Attempt<int> asInt = x.TryConvertTo<int>();
                    return asInt ? asInt.Result : int.MinValue;
                })
                .Where(x => x != int.MinValue && x != dataType.Id)
                .ToArray();

            return GetContainers(ancestorIds);
        }

        /// <inheritdoc/>
        public IEnumerable<EntityContainer> GetContainers(int[] containerIds)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                return _dataTypeContainerRepository.GetMany(containerIds);
            }
        }

        /// <inheritdoc/>
        public Attempt<OperationResult> SaveContainer(EntityContainer container, int userId = Cms.Core.Constants.Security.SuperUserId)
        {
            EventMessages eventMessages = EventMessagesFactory.Get();

            if (container.ContainedObjectType != Cms.Core.Constants.ObjectTypes.DataType)
            {
                var ex = new InvalidOperationException("Not a " + Cms.Core.Constants.ObjectTypes.DataType + " container.");
                return OperationResult.Attempt.Fail(eventMessages, ex);
            }

            if (container.HasIdentity && container.IsPropertyDirty("ParentId"))
            {
                var ex = new InvalidOperationException("Cannot save a container with a modified parent, move the container instead.");
                return OperationResult.Attempt.Fail(eventMessages, ex);
            }

            using (IScope scope = ScopeProvider.CreateScope())
            {
                var savingEntityContainerNotification = new EntityContainerSavingNotification(container, eventMessages);
                if (scope.Notifications.PublishCancelable(savingEntityContainerNotification))
                {
                    scope.Complete();
                    return OperationResult.Attempt.Cancel(eventMessages);
                }

                _dataTypeContainerRepository.Save(container);

                scope.Notifications.Publish(new EntityContainerSavedNotification(container, eventMessages).WithStateFrom(savingEntityContainerNotification));
                scope.Complete();
            }

            // TODO: Audit trail ?
            return OperationResult.Attempt.Succeed(eventMessages);
        }

        /// <inheritdoc/>
        public Attempt<OperationResult> DeleteContainer(int containerId, int userId = Cms.Core.Constants.Security.SuperUserId)
        {
            var evtMsgs = EventMessagesFactory.Get();
            using (IScope scope = ScopeProvider.CreateScope())
            {
                EntityContainer container = _dataTypeContainerRepository.Get(containerId);
                if (container == null)
                {
                    return OperationResult.Attempt.NoOperation(evtMsgs);
                }

                // 'container' here does not know about its children, so we need
                // to get it again from the entity repository, as a light entity
                IEntitySlim entity = _entityRepository.Get(container.Id);
                if (entity.HasChildren)
                {
                    scope.Complete();
                    return Attempt.Fail(new OperationResult(OperationResultType.FailedCannot, evtMsgs));
                }

                var deletingEntityContainerNotification = new EntityContainerDeletingNotification(container, evtMsgs);
                if (scope.Notifications.PublishCancelable(deletingEntityContainerNotification))
                {
                    scope.Complete();
                    return Attempt.Fail(new OperationResult(OperationResultType.FailedCancelledByEvent, evtMsgs));
                }

                _dataTypeContainerRepository.Delete(container);

                scope.Notifications.Publish(new EntityContainerDeletedNotification(container, evtMsgs).WithStateFrom(deletingEntityContainerNotification));
                scope.Complete();
            }

            // TODO: Audit trail ?
            return OperationResult.Attempt.Succeed(evtMsgs);
        }

        /// <inheritdoc/>
        public Attempt<OperationResult<OperationResultType, EntityContainer>> RenameContainer(int id, string name, int userId = Cms.Core.Constants.Security.SuperUserId)
        {
            EventMessages eventMessages = EventMessagesFactory.Get();
            using (IScope scope = ScopeProvider.CreateScope())
            {
                try
                {
                    EntityContainer container = _dataTypeContainerRepository.Get(id);

                    // throw if null, this will be caught by the catch and a failed returned
                    if (container == null)
                    {
                        throw new InvalidOperationException("No container found with id " + id);
                    }

                    container.Name = name;

                    var renamingEntityContainerNotification = new EntityContainerRenamingNotification(container, eventMessages);
                    if (scope.Notifications.PublishCancelable(renamingEntityContainerNotification))
                    {
                        scope.Complete();
                        return OperationResult.Attempt.Cancel(eventMessages, container);
                    }

                    _dataTypeContainerRepository.Save(container);
                    scope.Complete();

                    scope.Notifications.Publish(new EntityContainerRenamedNotification(container, eventMessages).WithStateFrom(renamingEntityContainerNotification));

                    return OperationResult.Attempt.Succeed(OperationResultType.Success, eventMessages, container);
                }
                catch (Exception ex)
                {
                    return OperationResult.Attempt.Fail<EntityContainer>(eventMessages, ex);
                }
            }
        }

        #endregion

        /// <summary>
        /// Gets a <see cref="IDataType"/> by its Name
        /// </summary>
        /// <param name="name">Name of the <see cref="IDataType"/></param>
        /// <returns><see cref="IDataType"/></returns>
        public IDataType GetDataType(string name)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                IDataType dataType = _dataTypeRepository.Get(Query<IDataType>().Where(x => x.Name == name)).FirstOrDefault();
                ConvertMissingEditorOfDataTypeToLabel(dataType);
                return dataType;
            }
        }

        /// <summary>
        /// Gets a <see cref="IDataType"/> by its Id
        /// </summary>
        /// <param name="id">Id of the <see cref="IDataType"/></param>
        /// <returns><see cref="IDataType"/></returns>
        public IDataType GetDataType(int id)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                IDataType dataType = _dataTypeRepository.Get(id);
                ConvertMissingEditorOfDataTypeToLabel(dataType);
                return dataType;
            }
        }

        /// <summary>
        /// Gets a <see cref="IDataType"/> by its unique guid Id
        /// </summary>
        /// <param name="id">Unique guid Id of the DataType</param>
        /// <returns><see cref="IDataType"/></returns>
        public IDataType GetDataType(Guid id)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                IQuery<IDataType> query = Query<IDataType>().Where(x => x.Key == id);
                IDataType dataType = _dataTypeRepository.Get(query).FirstOrDefault();
                ConvertMissingEditorOfDataTypeToLabel(dataType);
                return dataType;
            }
        }

        /// <summary>
        /// Gets a <see cref="IDataType"/> by its control Id
        /// </summary>
        /// <param name="propertyEditorAlias">Alias of the property editor</param>
        /// <returns>Collection of <see cref="IDataType"/> objects with a matching control id</returns>
        public IEnumerable<IDataType> GetByEditorAlias(string propertyEditorAlias)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                IQuery<IDataType> query = Query<IDataType>().Where(x => x.EditorAlias == propertyEditorAlias);
                IEnumerable<IDataType> dataType = _dataTypeRepository.Get(query);
                ConvertMissingEditorsOfDataTypesToLabels(dataType);
                return dataType;
            }
        }

        /// <summary>
        /// Gets all <see cref="IDataType"/> objects or those with the ids passed in
        /// </summary>
        /// <param name="ids">Optional array of Ids</param>
        /// <returns>An enumerable list of <see cref="IDataType"/> objects</returns>
        public IEnumerable<IDataType> GetAll(params int[] ids)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete: true))
            {
                var dataTypes = _dataTypeRepository.GetMany(ids);
                ConvertMissingEditorsOfDataTypesToLabels(dataTypes);
                return dataTypes;
            }
        }

        private void ConvertMissingEditorOfDataTypeToLabel(IDataType dataType)
        {
            if (dataType == null)
            {
                return;
            }

            ConvertMissingEditorsOfDataTypesToLabels(new[] { dataType });
        }

        private void ConvertMissingEditorsOfDataTypesToLabels(IEnumerable<IDataType> dataTypes)
        {
            // Any data types that don't have an associated editor are created of a specific type.
            // We convert them to labels to make clear to the user why the data type cannot be used.
            IEnumerable<IDataType> dataTypesWithMissingEditors = dataTypes
                .Where(x => x.Editor is MissingPropertyEditor);
            foreach (IDataType dataType in dataTypesWithMissingEditors)
            {
                dataType.Editor = new LabelPropertyEditor(_dataValueEditorFactory, _ioHelper);
            }
        }

        public Attempt<OperationResult<MoveOperationStatusType>> Move(IDataType toMove, int parentId)
        {
            EventMessages eventMessages = EventMessagesFactory.Get();
            var moveInfo = new List<MoveEventInfo<IDataType>>();

            using (IScope scope = ScopeProvider.CreateScope())
            {
                var moveEventInfo = new MoveEventInfo<IDataType>(toMove, toMove.Path, parentId);

                var movingDataTypeNotification = new DataTypeMovingNotification(moveEventInfo, eventMessages);
                if (scope.Notifications.PublishCancelable(movingDataTypeNotification))
                {
                    scope.Complete();
                    return OperationResult.Attempt.Fail(MoveOperationStatusType.FailedCancelledByEvent, eventMessages);
                }

                try
                {
                    EntityContainer container = null;
                    if (parentId > 0)
                    {
                        container = _dataTypeContainerRepository.Get(parentId);
                        if (container == null)
                        {
                            throw new DataOperationException<MoveOperationStatusType>(MoveOperationStatusType.FailedParentNotFound); // causes rollback
                        }
                    }

                    moveInfo.AddRange(_dataTypeRepository.Move(toMove, container));

                    scope.Notifications.Publish(new DataTypeMovedNotification(moveEventInfo, eventMessages).WithStateFrom(movingDataTypeNotification));

                    scope.Complete();
                }
                catch (DataOperationException<MoveOperationStatusType> ex)
                {
                    scope.Complete(); // TODO: what are we doing here exactly?
                    return OperationResult.Attempt.Fail(ex.Operation, eventMessages);
                }
            }

            return OperationResult.Attempt.Succeed(MoveOperationStatusType.Success, eventMessages);
        }

        /// <summary>
        /// Saves an <see cref="IDataType"/>
        /// </summary>
        /// <param name="dataType"><see cref="IDataType"/> to save</param>
        /// <param name="userId">Id of the user issuing the save</param>
        public void Save(IDataType dataType, int userId = Cms.Core.Constants.Security.SuperUserId)
        {
            EventMessages eventMessages = EventMessagesFactory.Get();
            dataType.CreatorId = userId;

            using (IScope scope = ScopeProvider.CreateScope())
            {
                var saveEventArgs = new SaveEventArgs<IDataType>(dataType);

                var savingDataTypeNotification = new DataTypeSavingNotification(dataType, eventMessages);
                if (scope.Notifications.PublishCancelable(savingDataTypeNotification))
                {
                    scope.Complete();
                    return;
                }

                if (string.IsNullOrWhiteSpace(dataType.Name))
                {
                    throw new ArgumentException("Cannot save datatype with empty name.");
                }

                if (dataType.Name != null && dataType.Name.Length > 255)
                {
                    throw new InvalidOperationException("Name cannot be more than 255 characters in length.");
                }

                _dataTypeRepository.Save(dataType);

                scope.Notifications.Publish(new DataTypeSavedNotification(dataType, eventMessages).WithStateFrom(savingDataTypeNotification));

                Audit(AuditType.Save, userId, dataType.Id);
                scope.Complete();
            }
        }

        /// <summary>
        /// Saves a collection of <see cref="IDataType"/>
        /// </summary>
        /// <param name="dataTypeDefinitions"><see cref="IDataType"/> to save</param>
        /// <param name="userId">Id of the user issuing the save</param>
        public void Save(IEnumerable<IDataType> dataTypeDefinitions, int userId)
        {
            EventMessages eventMessages = EventMessagesFactory.Get();
            IDataType[] dataTypeDefinitionsA = dataTypeDefinitions.ToArray();

            using (IScope scope = ScopeProvider.CreateScope())
            {
                var savingDataTypeNotification = new DataTypeSavingNotification(dataTypeDefinitions, eventMessages);
                if (scope.Notifications.PublishCancelable(savingDataTypeNotification))
                {
                    scope.Complete();
                    return;
                }

                foreach (IDataType dataTypeDefinition in dataTypeDefinitionsA)
                {
                    dataTypeDefinition.CreatorId = userId;
                    _dataTypeRepository.Save(dataTypeDefinition);
                }

                scope.Notifications.Publish(new DataTypeSavedNotification(dataTypeDefinitions, eventMessages).WithStateFrom(savingDataTypeNotification));

                Audit(AuditType.Save, userId, -1);

                scope.Complete();
            }
        }

        /// <summary>
        /// Deletes an <see cref="IDataType"/>
        /// </summary>
        /// <remarks>
        /// Please note that deleting a <see cref="IDataType"/> will remove
        /// all the <see cref="IPropertyType"/> data that references this <see cref="IDataType"/>.
        /// </remarks>
        /// <param name="dataType"><see cref="IDataType"/> to delete</param>
        /// <param name="userId">Optional Id of the user issuing the deletion</param>
        public void Delete(IDataType dataType, int userId = Cms.Core.Constants.Security.SuperUserId)
        {
            var eventMessages = EventMessagesFactory.Get();
            using (IScope scope = ScopeProvider.CreateScope())
            {
                var deletingDataTypeNotification = new DataTypeDeletingNotification(dataType, eventMessages);
                if (scope.Notifications.PublishCancelable(deletingDataTypeNotification))
                {
                    scope.Complete();
                    return;
                }

                // find ContentTypes using this IDataTypeDefinition on a PropertyType, and delete
                // TODO: media and members?!
                // TODO: non-group properties?!
                IQuery<PropertyType> query = Query<PropertyType>().Where(x => x.DataTypeId == dataType.Id);
                IEnumerable<IContentType> contentTypes = _contentTypeRepository.GetByQuery(query);
                foreach (IContentType contentType in contentTypes)
                {
                    foreach (PropertyGroup propertyGroup in contentType.PropertyGroups)
                    {
                        var types = propertyGroup.PropertyTypes.Where(x => x.DataTypeId == dataType.Id).ToList();
                        foreach (IPropertyType propertyType in types)
                        {
                            propertyGroup.PropertyTypes.Remove(propertyType);
                        }
                    }

                    // so... we are modifying content types here. the service will trigger Deleted event,
                    // which will propagate to DataTypeCacheRefresher which will clear almost every cache
                    // there is to clear... and in addition published snapshot caches will clear themselves too, so
                    // this is probably safe although it looks... weird.
                    //
                    // what IS weird is that a content type is losing a property and we do NOT raise any
                    // content type event... so ppl better listen on the data type events too.
                    _contentTypeRepository.Save(contentType);
                }

                _dataTypeRepository.Delete(dataType);

                scope.Notifications.Publish(new DataTypeDeletedNotification(dataType, eventMessages).WithStateFrom(deletingDataTypeNotification));

                Audit(AuditType.Delete, userId, dataType.Id);

                scope.Complete();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyDictionary<Udi, IEnumerable<string>> GetReferences(int id)
        {
            using (IScope scope = ScopeProvider.CreateScope(autoComplete:true))
            {
                return _dataTypeRepository.FindUsages(id);
            }
        }

        private void Audit(AuditType type, int userId, int objectId) =>
            _auditRepository.Save(new AuditItem(objectId, type, userId, ObjectTypes.GetName(UmbracoObjectTypes.DataType)));
    }
}
