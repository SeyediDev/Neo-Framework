"""Maintain source-backed MCP contract snapshots. Run --check in CI."""
from pathlib import Path
import argparse, hashlib, json
parser=argparse.ArgumentParser()
parser.add_argument('--check',action='store_true')
args=parser.parse_args()
comp=Path(__file__).resolve().parents[1]
repo=comp.parents[1]
knowledge=comp/'knowledge'
paths=[
'src/Neo.Endpoint/Controller/Base/GenericCrudResourceControllerBase.cs',
'src/Neo.Infrastructure/Features/Crud/CrudRuntimeDoctor.cs',
'src/Neo.Domain/Features/Concurrency/IConcurrencyVersion.cs',
'src/Neo.Application/Features/Crud/CrudDefinition.cs',
'src/Neo.Infrastructure/Features/Crud/NeoConcurrency.cs',
'src/Neo.Infrastructure/Features/Crud/EfCrudService.cs',
'src/Neo.Infrastructure/Features/Crud/CrudRegistration.cs',
'src/Neo.Application/Models/PagintationQuery.cs',
'src/Neo.Application/Features/GenericEntity/Queries/GetAllGenericEntity.cs',
'src/Neo.Application/Features/Outbox/IOutboxDeliveryStore.cs',
'src/Neo.Application/Features/Outbox/IOutboxExecutionJob.cs',
'src/Neo.Application/Features/Outbox/IProcessOutboxRecurringJob.cs',
'src/Neo.Application/Features/Outbox/Implementation/OutboxExecutionJob.cs',
'src/Neo.Application/Features/Outbox/Implementation/ProcessOutboxRecurringJob.cs',
'src/Neo.Application/Features/Outbox/Implementation/DefaultOutboxJobScheduler.cs',
'src/Neo.Infrastructure/Features/Outbox/EfOutboxStore.cs',
'src/Neo.Infrastructure/Features/Messaging/SqlServerOutboxExtensions.cs',
'src/Neo.Application/Features/Outbox/Implementation/OutboxMessageProcessor.cs',
'src/Neo.Application/Features/Outbox/IdempotencyKeyHasher.cs',
'src/Neo.Domain/Entities/Common/OutboxMessage.cs',
'src/Neo.Domain/Features/Cache/IAtomicCacheService.cs',
'src/Neo.Infrastructure/Features/Outbox/RedisDistributedLock.cs',
'src/Neo.Infrastructure/Features/Outbox/IdempotencyStoreRedis.cs',
'src/Neo.Infrastructure/Features/Outbox/IdempotencyStoreWithCacheService.cs',
'src/Neo.Application/Behaviours/MediatR/PublishEventsOfCommandsBehaviour.cs',
'src/Neo.Infrastructure/Features/Queue/Hangfire/HangfireJobExecuter.cs',
'src/Neo.Infrastructure/Features/Queue/Hangfire/HangfireOptions.cs',
'src/Neo.Infrastructure/Features/Queue/Hangfire/HangfireServiceCollectionExtensions.cs',
'src/Neo.Domain/Entities/Base/IDomainEventEntity.cs',
'src/Neo.Infrastructure/Data/Interceptors/DispatchDomainEventsInterceptor.cs',
'src/Neo.Infrastructure/Features/Messaging/NeoRabbitMqOptions.cs',
'src/Neo.Infrastructure/Features/Messaging/RabbitMqServiceCollectionExtensions.cs',
'src/Neo.Domain/Repository/IRepository.cs',
'src/Neo.Domain/Repository/ICommandRepository.cs',
'src/Neo.Domain/Repository/IQueryRepository.cs',
'src/Neo.Domain/Repository/IUnitOfWork.cs',
'src/Neo.Domain/Entities/Base/BaseEntity.cs',
'src/Neo.Infrastructure/Data/Repository/Ef/EfRepositoryBase.cs',
'src/Neo.Application/Features/GenericEntity/Commands/CreateGenericEntity.cs',
'src/Neo.Application/Features/GenericEntity/Commands/UpdateGenericEntity.cs',
'src/Neo.Application/Features/GenericEntity/Commands/DeleteGenericEntity.cs',
'src/Neo.Endpoint/Controller/Base/GenericCrudControllerBase.cs',
'src/Neo.Domain/Features/Telementry/TelementryBehaviour.cs',
'src/Neo.Domain/Features/Telementry/TelementryObject.cs',
'src/Neo.Domain/Features/Telementry/TelemetryAttribute.cs',
'src/Neo.Domain/Features/Telementry/TelemetryInterceptor.cs',
'src/Neo.Domain/Features/Telementry/TelemetryInvocation.cs',
'src/Neo.Domain/Features/Telementry/TelemetryProxyFactory.cs',
'src/Neo.Domain/Features/Telementry/TelemetryProxy.cs',
'src/Neo.Domain/Features/Telementry/TelemetryDispatchProxy.cs',
'src/Neo.Domain/Features/Telementry/ServiceCollectionExtensions.cs',
'src/Neo.Domain/Features/Telementry/TelemetryOptions.cs',
'src/Neo.Domain/DependencyInjection.cs',
'src/Neo.Infrastructure/Features/Telementry/DependencyInjection.cs']
entries=[]
for path in paths:
    # Canonical LF encoding makes the snapshot independent of Git autocrlf.
    text=(repo/path).read_text(encoding='utf-8-sig')
    data=text.encode('utf-8')
    entries.append({'path':path,'sha256':hashlib.sha256(data).hexdigest()})
    destination=knowledge/'contracts'/path
    if args.check:
        assert destination.exists() and destination.read_text(encoding='utf-8-sig')==text, 'Stale contract: '+path
    else:
        destination.parent.mkdir(parents=True,exist_ok=True)
        destination.write_text(text,encoding='utf-8',newline='\n')
fingerprint=hashlib.sha256(json.dumps(entries,sort_keys=True,separators=(',',':')).encode()).hexdigest()
manifest={'baseline':'sha256:'+fingerprint,'repository':'https://github.com/SeyediDev/Neo-Framework',
    'baseCommit':'e34c0ad5336c71e0cf3be4bccb7f425aecccea9d',
    'versionPolicy':'Exact normalized source-contract snapshot; baseCommit is provenance, not a claim that this is an unchanged commit.',
    'sources':entries}
file=knowledge/'manifest.json'
if args.check: assert json.loads(file.read_text(encoding='utf-8-sig'))==manifest, 'Stale manifest; run sync_knowledge.py'
else:file.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
print(('Verified' if args.check else 'Updated')+' '+manifest['baseline'])
