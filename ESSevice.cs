using Elasticsearch.Net;
using Microsoft.Extensions.Options;
using Nest;
using System;
using System.Collections.Concurrent;
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

    /// <summary>
    /// 访问ElasticSearch服务类
    /// </summary>
    /// <summary>
    /// 访问ElasticSearch服务类
    /// </summary>
    public class ESService
    {
        /// <summary>
        /// Linq查询的官方Client
        /// </summary>
        public ElasticClient Client { get; set; }

        /// <summary>
        ///
        /// </summary>
        /// <param name="esConfig"></param>
        public ESService(IOptions<ESConfig> esConfig) => GetClient(esConfig.Value);

        /// <summary>
        ///
        /// </summary>
        /// <param name="esConfig"></param>
        public ESService(ESConfig esConfig) => GetClient(esConfig);

        /// <summary>
        /// 获取客户端
        /// </summary>
        /// <param name="esConfig"></param>
        /// <returns></returns>
        public ElasticClient GetClient(ESConfig esConfig)
        {
            var uris = esConfig.Urls.Split(',').ToList().Select(i => Uri.TryCreate(i, UriKind.Absolute, out Uri u) ? u : null);//配置节点地址，以，分开
            var settings = new ConnectionSettings(new StaticConnectionPool(uris))
                .BasicAuthentication(esConfig.User, esConfig.Pwd)//用户名和密码
                .RequestTimeout(TimeSpan.FromSeconds(30));//请求配置参数
            Client = new ElasticClient(settings);//linq请求客户端初始化
            return Client;
        }

        #region 索引

        /// <summary>
        /// 索引本地缓存的最大时间，超时则清理
        /// </summary>

        public static int MaxAgeOfIndexCacheInMinutes = 30;

        /// <summary>
        /// 判断索引是否存在(优先检测缓存)
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="selector"></param>
        /// <returns></returns>
        public bool ExistsIndex(string indexName, Func<IndexExistsDescriptor, IIndexExistsRequest> selector = null)
        {
            indexName = indexName.ToLower();
            bool existed = false;


            if (ExistedIndexCache.TryGetValue(indexName, out DateTime dt) && (DateTime.Now - dt).TotalMinutes < MaxAgeOfIndexCacheInMinutes)
                existed = true;
            else
            {
                existed = Client.Indices.Exists(indexName, selector).Exists;
                if (existed) ExistedIndexCache.AddOrUpdate(indexName, DateTime.Now, (k, v) => DateTime.Now);
                else ExistedIndexCache.TryRemove(indexName, out _);
            }

            return existed;
        }

        private ConcurrentDictionary<string, DateTime> ExistedIndexCache = new();

        /// <summary>
        /// 创建索引
        /// </summary>
        /// <param name="indexName">索引名</param>
        /// <param name="numberOfReplicas">副本数量</param>
        /// <param name="numberOfShards">分片数量</param>
        /// <returns></returns>
        public async Task<CreateIndexResponse> CreateIndex(string indexName, int numberOfReplicas = 1, int numberOfShards = 5)
        {
            indexName = indexName.ToLower();
            IIndexState indexState = new IndexState
            {
                Settings = new IndexSettings
                {
                    NumberOfReplicas = numberOfReplicas,
                    NumberOfShards = numberOfShards
                }
            };
            CreateIndexResponse response = await Client.Indices.CreateAsync(indexName, x => x.InitializeUsing(indexState).Map(m => m.AutoMap()));
            return response;
        }

        /// <summary>
        /// 删除索引
        /// </summary>
        /// <param name="indexName"></param>
        public DeleteIndexResponse DeleteIndex(string indexName)
        {
            DeleteIndexResponse response = Client.Indices.Delete(indexName);

            return response;
        }

        #endregion 索引

        #region -- 文档

        /// <summary>
        /// 创建文档。会先检查索引是否存在，然后再创建。
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="entity"></param>
        /// <param name="indexName"></param>
        /// <returns>如果索引不存在且创建失败则可能返回null</returns>
        public async Task<CreateResponse> CreateDocument<T>(T entity, string indexName) where T : class
        {
            CreateResponse response = null;

            //索引不存在，就创建索引。
            bool hasIndex = ExistsIndex(indexName);
            if (hasIndex) response = await Client.CreateAsync(entity, t => t.Index(indexName.ToLower()));
            else response = await CreateDataStream(indexName)
                    .ContinueWith(t => t.Result?.IsValid == true ? Client.CreateAsync(entity, t => t.Index(indexName.ToLower())).Result : null);

            return response;
        }

        /// <summary>
        /// 创建数据流
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="entity"></param>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public async Task<CreateDataStreamResponse> CreateDataStream(string dataStreamName) => await Client.Indices.CreateDataStreamAsync(dataStreamName);

        /// <summary>
        /// 获取文档
        /// </summary>
        public async Task<ISearchResponse<T>> GetDocument<T>(string indexName, int startIndex = 0, int size = 10, bool sortDescending = false, string sortField = "@timestamp") where T : class
        {
            return await Client.SearchAsync<T>(r => r.Index(indexName.ToLower())
           .Sort(i => sortDescending ? i.Descending(new Field(sortField)) : i.Ascending(new Field(sortField)))
           .From(startIndex)
           .Size(size));
        }

        #endregion -- 文档
    }
}