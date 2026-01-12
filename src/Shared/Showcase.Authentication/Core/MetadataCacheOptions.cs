using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Showcase.Authentication.Core;
public class MetadataCacheOptions
{
    public bool EnableMetadataCache { get; set; } = false;

    /// <summary>
    /// Gets or sets the cache duration for the protected resource metadata.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the cache key prefix for the protected resource metadata.
    /// </summary>
    public string CacheKeyPrefix { get; set; } = string.Empty;
}
