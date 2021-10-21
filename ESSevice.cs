using Elasticsearch.Net;
using Microsoft.Extensions.Options;
using Nest;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
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
            this.Client = new ElasticClient(settings);//linq请求客户端初始化
            return this.Client;
        }

        #region 索引

        /// <summary>
        /// 判断索引是否存在
        /// </summary>
        /// <param name="indexName"></param>
        /// <param name="selector"></param>
        /// <returns></returns>
        public Task<bool> ExistsIndex(string indexName, Func<IndexExistsDescriptor, IIndexExistsRequest> selector = null)
        {
            indexName = indexName.ToLower();
            bool existed = false;
            if (ExistedIndexCache.TryGetValue(indexName, out _)) existed = true;
            else
            {
                var existResponse = this.Client.Indices.Exists(indexName, selector);
                existed = existResponse.Exists;
                if (existed) ExistedIndexCache.AddOrUpdate(indexName, DateTime.Now, (k, v) => DateTime.Now);
            }
            return Task.FromResult(existed);
        }

        private ConcurrentDictionary<string, DateTime> ExistedIndexCache = new ConcurrentDictionary<string, DateTime>();

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
            CreateIndexResponse response = await this.Client.Indices.CreateAsync(indexName, x => x.InitializeUsing(indexState).Map(m => m.AutoMap()));
            return response;
        }

        /// <summary>
        /// 删除索引
        /// </summary>
        /// <param name="indexName"></param>
        public DeleteIndexResponse DeleteIndex(string indexName)
        {
            DeleteIndexResponse response = this.Client.Indices.Delete(indexName);

            return response;
        }

        #endregion 索引

        #region -- 文档

        /// <summary>
        /// 创建文档
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <param name="entity"></param>
        /// <param name="indexName"></param>
        /// <returns></returns>
        public async Task<CreateResponse> CreateDocument<TEntity>(TEntity entity, string indexName) where TEntity : class
        {
            CreateResponse response = await this.Client.CreateAsync(entity, t => t.Index(indexName.ToLower()));
            return response;
        }


        ///// <summary>
        ///// 获取文档
        ///// </summary>        
        //public async Task<CreateResponse> GetDocument<T>(T entity, string indexName) where T : class
        //{
        //    //CreateResponse response = await this.Client.SearchAsync()
        //    //return response;
        //}


        #endregion -- 文档
    }
}