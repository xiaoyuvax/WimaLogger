using Elasticsearch.Net;
using Microsoft.Extensions.Options;
using Nest;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    //This is a small bug in Visual Studio 2019 that hasn't been fixed yet. To solve this, you need to add a dummy class named IsExternalInit with the namespace System.Runtime.CompilerServices anywhere in your project.
    //If writing a library it's best to make this class internal, as otherwise you can end up with two libraries both defining the same type.
    internal static class IsExternalInit { }
}

namespace Wima.Log
{
    /// <summary>
    /// ES配置
    /// </summary>
    public record ESConfig(string Urls, string User, string Pwd);
    public record UpdateAllResult(int? ThisBatchNo = null, int? TrialCount = null, int? ErrCode = null, string Msg = null);

    public record UpdateAllWatchParams(string IndexName, int? ThisBatchNo = null, int? DocCntInIndex = null, int? DocCntInBatch = null, int? subIndexCnt = null, int? subIndexNo = null, int? TrialCount = null, int? ThreadsLimit = null, int? TrialStack = null, int? ErrCode = null, string Msg = null)
        : UpdateAllResult(ThisBatchNo, TrialCount, ErrCode, Msg);

    internal class FakeCreateResponse : CreateResponse
    {
        private bool _isValid = false;

        public FakeCreateResponse(bool isValid)
        {
            _isValid = isValid;
        }

        public override bool IsValid => _isValid;
    }

    /// <summary>
    /// 访问ElasticSearch服务类
    /// </summary>
    /// <summary>
    /// 访问ElasticSearch服务类
    /// </summary>
    public partial class ESService
    {
        public const string TimeFormat = "HH:mm:ss";
        private readonly object syncExistIndex = new object();
        private ConcurrentDictionary<string, DateTime> _indexCache = new ConcurrentDictionary<string, DateTime>();

        public ESService(IOptions<ESConfig> esConfig) => CreateClient(esConfig.Value);

        public ESService(ESConfig esConfig) => CreateClient(esConfig);

        /// <summary>
        /// Linq查询的官方Client
        /// </summary>
        public ElasticClient Client { get; set; }

        /// <summary>
        /// 索引本地缓存的最大时间，超时则清理
        /// </summary>
        public int MaxAgeOfIndexCacheInMinutes { get; set; } = 30;

        /// <summary>
        /// 获取客户端
        /// </summary>
        /// <param name="esConfig"></param>
        /// <returns></returns>
        public ElasticClient CreateClient(ESConfig esConfig)
        {
            if (esConfig is null)
            {
                throw new ArgumentNullException(nameof(esConfig));
            }

            var uris = esConfig.Urls.Split(',').ToList().Select(i => Uri.TryCreate(i, UriKind.Absolute, out Uri u) ? u : null);//配置节点地址，以，分开
            var settings = new ConnectionSettings(new StaticConnectionPool(uris))
                .BasicAuthentication(esConfig.User, esConfig.Pwd)//用户名和密码
                .RequestTimeout(TimeSpan.FromSeconds(30));//请求配置参数

            //if (!esConfig.IdInference) settings.DefaultMappingFor<OrderDoc>(m => m.DisableIdInference());
            Client = new ElasticClient(settings);//linq请求客户端初始化
            return Client;
        }

        #region 索引

        /// <summary>
        /// 创建索引
        /// </summary>
        /// <param name="indexName">索引名</param>
        /// <param name="numberOfReplicas">副本数量</param>
        /// <param name="numberOfShards">分片数量</param>
        /// <returns></returns>
        public async Task<CreateIndexResponse> CreateIndex(string indexName, int numberOfReplicas = 1, int numberOfShards = 5, int refreshInterval = 1, string alias = null)
        {
            indexName = indexName.ToLower();
            IIndexState indexState = new IndexState
            {
                Settings = new IndexSettings()
                {
                    NumberOfReplicas = numberOfReplicas,
                    NumberOfShards = numberOfShards,
                    RefreshInterval = refreshInterval,
                }
            };
            CreateIndexResponse response = await Client.Indices.CreateAsync(indexName, x => x.InitializeUsing(indexState).Map(m => m.AutoMap()).Aliases(a => alias == null ? a : a.Alias(alias)));

            return response;
        }

        /// <summary>
        /// 确保数据流存在
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="clean"></param>
        /// <returns></returns>
        public bool EnsureDS(string indexName, bool clean = false)
        {
            bool hasIndex;
            lock (Client)
            {
                hasIndex = ExistsIndex(indexName);
                if (clean && hasIndex)
                {
                    if (Client.Indices.DeleteDataStream(indexName).IsValid) hasIndex = false;
                }
                if (!hasIndex)
                {
                    Client.Indices.CreateDataStream(indexName);
                    hasIndex = ExistsIndex(indexName);
                }
            }

            return hasIndex;
        }

        /// <summary>
        /// 确保索引存在
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="alias"></param>
        /// <param name="replicaNo"></param>
        /// <param name="refreshInterval"></param>
        /// <param name="clean"></param>
        /// <returns></returns>
        public bool EnsureIndex(string indexName, string alias = null, int replicaNo = 1, int refreshInterval = 1, bool clean = false)
        {
            bool hasIndex = ExistsIndex(indexName);
            if (clean && hasIndex)
            {
                if (Client.Indices.Delete(indexName).IsValid) hasIndex = false;
            }
            if (!hasIndex)
            {
                CreateIndex(indexName, replicaNo, refreshInterval: refreshInterval, alias: alias).Wait();
                hasIndex = ExistsIndex(indexName);
            }

            return hasIndex;
        }

        /// <summary>
        /// 判断索引是否存在并更新缓存(优先检测缓存)
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="selector"></param>
        /// <returns></returns>
        public bool ExistsIndex(string indexName, Func<IndexExistsDescriptor, IIndexExistsRequest> selector = null)
        {
            indexName = indexName.ToLower();
            bool existed = false;

            if (_indexCache.TryGetValue(indexName, out DateTime dt) && (DateTime.Now - dt).TotalMinutes < MaxAgeOfIndexCacheInMinutes)
                existed = true;
            else
            {
                lock (syncExistIndex) existed = Client.Indices.Exists(indexName, selector).Exists;
                if (existed) _indexCache.AddOrUpdate(indexName, DateTime.Now, (k, v) => DateTime.Now);
                else _indexCache.TryRemove(indexName, out _);
            }
            return existed;
        }

        #endregion 索引

        #region 文档

        public const int MAX_DOC_CACHE_SIZE = 20;
        public const int MAX_DOC_CACHE_WAIT_IN_SECONDS = 10;
        public List<string> GlobalAllIndex = new List<string>();
        private ConcurrentDictionary<string, ConcurrentDictionary<long, object>> CacheBook = new ConcurrentDictionary<string, ConcurrentDictionary<long, object>>();
        //ticks

        /// <summary>
        /// 获取文档
        /// </summary>
        public async Task<ISearchResponse<T>> GetDocument<T>(string indexName, int startIndex = 0, int size = 10, bool sortDescending = false, string sortField = "@timestamp", DateTime startTime = default, DateTime endTime = default) where T : class
        {
            return await Client.SearchAsync<T>(r => r.Index(indexName.ToLower())
            .Sort(i => sortDescending ? i.Descending(new Field(sortField)) : i.Ascending(new Field(sortField)))
            .From(startIndex)
            .Size(size).TrackTotalHits(true).FilterPath("-_shards", "-metadata")
            .Query(q => q.DateRange(d =>
            {
                var r = d.Field("@timestamp");
                if (startTime != default) r.GreaterThanOrEquals(startTime);
                if (endTime != default) r.LessThanOrEquals(endTime);
                return r;
            }))
            );
        }

        /// <summary>
        /// 创建文档。会先检查索引是否存在，然后再创建。
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="entity"></param>
        /// <param name="indexName"></param>
        /// <returns>如果索引不存在且创建失败则可能返回null</returns>
        public async Task<CreateResponse> Index<T>(T entity, string indexName, string alias = null) where T : class
        {
            CreateResponse response = null;
            try
            {
                if (EnsureIndex(indexName, alias)) response = await Client.CreateAsync(entity, t => t.Index(indexName.ToLower()));
            }
            catch (Exception ex)
            {
            }
            return response;
        }

        /// <summary>
        /// 批量创建文档。会先检查索引是否存在，然后再创建。
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="docs"></param>
        /// <param name="indexName"></param>
        /// <returns>如果索引不存在且创建失败则可能返回null</returns>
        public BulkAllObservable<T> IndexAll<T>(IEnumerable<T> docs, string indexName, string alias = null, int size = 50000, Func<BulkResponseItemBase, T, BulkAllDescriptor<T>> droppedDocCallBack = null) where T : class
        {
            BulkAllObservable<T> response = null;
            int c = 0;
            try
            {
                if (ExistsIndex(indexName)) response = Client.BulkAll(docs, t1 => t1.Index(indexName)
                       .BackOffRetries(20)
                       .BackOffTime("10s")
                       .BackPressure(1, 2)
                       .MaxDegreeOfParallelism(Environment.ProcessorCount)
                       .RefreshOnCompleted()
                       .DroppedDocumentCallback((bulkResponseItem, doc) => droppedDocCallBack?.Invoke(bulkResponseItem, doc)
                       )
                       .Size(size)
                      );
            }
            catch (Exception ex)
            {
                throw ex;
            }

            return response;
        }

        /// <summary>
        /// 批量执行一个批次的更新操作(不会检查索引是否存在)。
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="e"></param>
        /// <param name="eoc"></param>
        /// <param name="batchSize"></param>
        /// <returns></returns>
        public BulkResponse IndexBulk<T>(IEnumerable<T> e, string indexName, bool noItems = false) where T : class =>
            Client.Bulk(s => s.Index(indexName).IndexMany<T>(e, (d, adoc) => d.Document(adoc)).FilterPath("-_shards", "-metadata", noItems ? "-items" : "items"));

        /// <summary>
        /// 在数据流中创建文档。会先检查数据流是否存在，不存在就创建。
        /// 注意：数据流无法使用ES批量提交。
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="entity"></param>
        /// <param name="indexName"></param>
        /// <returns>如果索引不存在且创建失败，暂时缓存或批量提交则可能返回null</returns>
        public async Task<CreateResponse> IndexDS<T>(T entity, string indexName) where T : class
        {
            CreateResponse response = new FakeCreateResponse(false);

            //索引不存在，就创建索引。
            if (EnsureDS(indexName)) response = await Client.CreateAsync(entity, t => t.Index(indexName.ToLower()));

            return response;
        }

        /// <summary>
        /// 在创建文档后使索引可搜索
        /// </summary>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public async Task<RefreshResponse> Refresh(string indexName) => await Client.Indices.RefreshAsync(indexName);

        /// <summary>
        /// 获取文档(这只是一个示例)
        /// </summary>
        public async Task<ISearchResponse<T>> SearchAsync<T>(string indexName, int startIndex = 0, int? size = null, bool sortDescending = false, string? sortField = null, Func<QueryContainerDescriptor<T>, QueryContainer> query = null, Func<AggregationContainerDescriptor<T>, IAggregationContainer> aggSelector = null) where T : class
        {
            return await Client.SearchAsync<T>(r =>
            {
                r = r.Index(indexName.ToLower()).From(startIndex).TrackTotalHits(true).FilterPath("-_shards", "-metadata");
                if (size == null) r = r.MatchAll();
                else r = r.Size(size);
                if (sortField != null) r = r.Sort(i => sortDescending ? i.Descending(new Field(sortField)) : i.Ascending(new Field(sortField)));
                if (query != null) r = r.Query(query);
                if (aggSelector != null) r = r.Aggregations(aggSelector);

                return r;
            });
        }

        /// <summary>
        /// 更新单个订单文档
        /// </summary>
        /// <param name="doc"></param>
        /// <returns></returns>
        public async Task<UpdateResponse<T>> Update<T>(T doc) where T : IndexableDoc => await Client.UpdateAsync<T>(doc.Id, s => s.Upsert(doc));

        /// <summary>
        /// 批量执行一个批次的更新操作(不会检查索引是否存在)。
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="e"></param>
        /// <param name="eoc"></param>
        /// <param name="batchSize"></param>
        /// <returns></returns>
        public BulkResponse UpdateBulk<T>(IEnumerable<T> e, string indexName, bool upsert = true, bool noItems = false) where T : class =>
            Client.Bulk(s => s.Index(indexName).UpdateMany<T>(e, (d, adoc) => d.Doc(adoc).DocAsUpsert(upsert)).FilterPath("-_shards", "-metadata", noItems ? "-items" : "items"));  //FilterPath可优化性能

        //FilterPath可优化性能

        #endregion 文档

        #region ES设置

        /// <summary>
        /// 暂存上一个刷新间隔值
        /// </summary>
        public ConcurrentDictionary<string, Time> DictLastRefreshInterval { get; set; } = new ConcurrentDictionary<string, Time>();

        /// <summary>
        /// 暂存上一个副本数
        /// </summary>
        public ConcurrentDictionary<string, int?> DictLastReplicaNumber { get; set; } = new ConcurrentDictionary<string, int?>();

        public GetIndexSettingsResponse SetRefreshInterval(string indexName, Time refreshInterval, GetIndexSettingsResponse settingResponse = null)
        {
            settingResponse ??= Client.Indices.GetSettings(indexName);
            if (settingResponse.IsValid)
            {
                DictLastRefreshInterval.TryAdd(indexName, settingResponse.Indices[indexName].Settings.RefreshInterval);
                Client.Indices.UpdateSettings(indexName, s => s.IndexSettings(se => se.RefreshInterval(refreshInterval)));
                return settingResponse;
            }
            return null;
        }

        public GetIndexSettingsResponse SetReplica(string indexName, int? replica = 1, GetIndexSettingsResponse settingResponse = null)
        {
            settingResponse ??= Client.Indices.GetSettings(indexName);
            if (settingResponse.IsValid)
            {
                DictLastReplicaNumber.TryAdd(indexName, settingResponse.Indices[indexName].Settings.NumberOfReplicas);
                Client.Indices.UpdateSettings(indexName, s => s.IndexSettings(se => se.NumberOfReplicas(replica)));
                return settingResponse;
            }
            return null;
        }

        #endregion ES设置
    }
}