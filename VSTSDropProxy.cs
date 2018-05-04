using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

using Microsoft.Extensions.Logging;
using Polly;

namespace DropDownloadCore
{
    //use vsts drop rest api for manifest and blob and a PAT to grab urls to files and hackily materialize them.
    public class VSTSDropProxy 
    {
        // sigh these went public moths ago. Check if we can use non preview versions
        private const string ManifestAPIVersion = "2.0-preview";
        private const string BlobAPIVersion = "2.1-preview";

        private readonly IDropApi _dropApi = null;
        private readonly HttpClient _contentClient = new HttpClient();

        private readonly Uri _VSTSDropUri;
        private readonly string _relativeroot;
        
        private readonly Dictionary<string, string> _pathToUrl = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<string, UniqueFile> _blobs = new Dictionary<string, UniqueFile>(StringComparer.InvariantCultureIgnoreCase);
  
        private readonly ILoggerFactory _loggerFactory; // might not need to hold this past ctor?
        private readonly ILogger _logger;

        public VSTSDropProxy(string VSTSDropUri, string path, string pat, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<VSTSDropProxy>();

            //mLogger = logger;
            _dropApi = new RestfulDropApi(pat, _loggerFactory);
              
            if (!Uri.TryCreate(VSTSDropUri, UriKind.Absolute, out _VSTSDropUri))
            {
                throw new ArgumentException($"VSTS drop URI invalid {VSTSDropUri}", nameof(VSTSDropUri));
            }
            
            if (path == null)
            {
                throw new ArgumentException($"VSTS drop URI must contain a ?root= querystring {_VSTSDropUri}", nameof(VSTSDropUri));
            }
            
            _relativeroot = path.Replace("\\", "/");
            if (!_relativeroot.StartsWith("/"))
                _relativeroot = "/" + _relativeroot;
            if (!_relativeroot.EndsWith("/"))
                _relativeroot = _relativeroot + "/";

            //move this to a lazy so we can actually be async?
            IEnumerable<VstsFile> files = null;
            try
            {
                var manifesturi = Munge(_VSTSDropUri, ManifestAPIVersion);
                 files = _dropApi.GetVstsManifest(manifesturi, BlobAPIVersion, _relativeroot ).Result;
            }
            catch (Exception dropApiException) // gotta catch 'em all! -ash ketchexception.
            {
                throw new DropException($"Not able to get build manifest please check your build '{VSTSDropUri}'", dropApiException);
            }

            // dictionary doesn't necesarily make sesne now.
            // clocke: so what does?
            VstsFilesToDictionary(VSTSDropUri, files);
            _logger.LogInformation($"Found {_pathToUrl.Count} files, {_blobs.Count} unique.");
        }

        /// <summary>
        /// Gets the manifest uri from the drop url
        /// </summary>
        /// <param name="vstsDropUri">The drop url.</param>
        /// <param name="apiVersion">API version to request.</param>
        /// <returns>The manifest uri.</returns>
        private static Uri Munge(Uri vstsDropUri, string apiVersion)
        {
            var querystring = HttpUtility.ParseQueryString(vstsDropUri.Query);
            string manifestpath = vstsDropUri.AbsolutePath.Replace("_apis/drop/drops", "_apis/drop/manifests");
            var uriBuilder = new UriBuilder(vstsDropUri.Scheme, vstsDropUri.Host, -1, manifestpath);
            var queryParameters = HttpUtility.ParseQueryString(uriBuilder.Query);
            queryParameters.Add(RestfulDropApi.APIVersionParam,apiVersion);
            uriBuilder.Query = queryParameters.ToString();
            
            return uriBuilder.Uri;
        }

        private void VstsFilesToDictionary(string VSTSDropUri, IEnumerable<VstsFile> files)
        {
            if (!files.Any())
            {
                throw new Exception("Encountered empty build drop check your build " + VSTSDropUri);
            }

            // two iterations on files to check for badness and then do actual work below,
            // could/should we combine?
            var invalidFiles = false;
            foreach (var f in files)
            {
                if (!f.IsValid)
                {
                    invalidFiles = true;
                    _logger.LogError($"Invalid VSTS Files for {VSTSDropUri}: {f.Path} - {f.Blob.Url}");
                }
            }
            if (invalidFiles)
            {
                throw new DropException($"invalid VSTS files for {VSTSDropUri}.");
            }

            //cant do to dictionary because of duplicate binplacing. Cloudbuild can be set to break on this.
            //https://1eswiki.com/wiki/CloudBuild_Duplicate_Binplace_Detection
            foreach (var file in files)
            {
                _logger.LogDebug($"{file.Path}->{file.Blob.Url}");
                if (_pathToUrl.ContainsKey(file.Path))
                {
                    continue;
                }
                //ignore blobid/hash -- goodbye caching and verification
                _pathToUrl[file.Path] = file.Blob.Url;

                if (!_blobs.ContainsKey(file.Blob.Id))
                {
                    _blobs[file.Blob.Id] = new UniqueFile { 
                        Url = file.Blob.Url,
                        Paths = new List<string> { file.Path }
                    };
                }
                else
                {
                     _blobs[file.Blob.Id].Paths.Add( file.Path );
                }
            }

            _logger.LogInformation($"Found {_pathToUrl.Count} files, {_blobs.Count} unique");
            int pathcount = _pathToUrl.Count;
                int blobcount = _blobs.SelectMany(kvp => kvp.Value.Paths).Count();
                if (pathcount != blobcount)
                {
                    throw new DropException($"_pathToUrl {pathcount} != _blob {blobcount}");            
                }
        }

        private async Task Download(string sasurl, string localpath)
        {
            await Policy
                .Handle<HttpRequestException>()
                .Or<IOException>()
                .Or<TaskCanceledException>()
                .WaitAndRetryAsync(5, 
                    retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    (e,t) => 
                    {
                        _logger.LogError(e, $"Exception {e} on {sasurl} -> {localpath}");
                        File.Delete(localpath);
                    })
                .ExecuteAsync(async () =>
                {
                    using (var blob = await _contentClient.GetStreamAsync(sasurl).ConfigureAwait(false))
                    using (var fileStream = new FileStream(localpath, FileMode.CreateNew))
                    {
                        await blob.CopyToAsync(fileStream).ConfigureAwait(false);
                    }
                });
        }
        
         //So this only dedupes within a build.
         //other options for perf.
         // 1) only grab certain directories either with dockerfiles or as specified by build.yaml
         // 2) Prioritize large files or files with lots of copies.
         // 3) linux symlink instead of copy
         // 4) parallelize copy with buffer first attempt at that with _contentClient.GetBufferAsync failed. Also lots of memory.
         // 5) multistream copyasync
         // Tried configureawait(false) and copyaync to each file (though not aparallelized) with no effect.
        public async Task Materialize(string localDestiantion)
        {
            //precreate directories so we don't have to worry.
            //slow but we don't care cause we want to move off and have sangam use blobs directly
            foreach (var file in _pathToUrl) //could do blobs.selectmany(vallues)
            {
                var localFileName = file.Key.Substring(_relativeroot.Length);
                var localpath = Path.Combine(localDestiantion,localFileName).Replace("\\","/");
                //also not efficient to check directory each time but again this method is a hack.
                Directory.CreateDirectory(Path.GetDirectoryName(localpath));
            }

            int downloaded = 0;
            var downloads = _blobs.Select(async file => 
            {
                if (!file.Value.Paths.Any())
                {
                    // should this be DropException?
                    throw new ArgumentException($"empty : {file.Key}");
                }
                
                var firstPath = file.Value.Paths.First();
                var localFilename = firstPath.Substring(_relativeroot.Length);
                var localPath = Path.Combine(localDestiantion,localFilename).Replace("\\","/");
                await Download(file.Value.Url, localPath);
                 
                // parallelize this too? worth it?
                foreach (var other in file.Value.Paths.Skip(1))
                {
                    var otherFileName = other.Substring(_relativeroot.Length);
                    var otherpath = Path.Combine(localDestiantion,otherFileName).Replace("\\","/");
                    File.Copy(localPath, otherpath);
                }
                if (++downloaded % 100 == 0)
                {
                    _logger.LogInformation($"Downloaded {downloaded} files");
                }
                
            });

            await Task.WhenAll(downloads);
        } 
    }

    struct UniqueFile
    {
        public string Url;
        public List<string> Paths;
    }

    // Helper classes for parsing VSTS drop exe output lowercase to match json output
    public sealed class VstsFile
    {
        public string Path { get; set; }
        public VstsBlob Blob { get; set; }

        public bool IsValid {
            get
            {
                return !string.IsNullOrWhiteSpace(Path) && Blob.IsValid;
            }
        }
    }

    public sealed class VstsBlob
    {
        public string Url;
        public string Id;

        public bool IsValid
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Id);
            }
        }
    }
}
