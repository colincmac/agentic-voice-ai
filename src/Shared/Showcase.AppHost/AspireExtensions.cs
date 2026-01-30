using System;
using System.Collections.Generic;
using System.Text;
using Aspire.Hosting.Azure;

namespace Showcase.AppHost;

public static class AspireExtensions
{
    //public static IResourceBuilder<T> AsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
    //where T : IAzureResource
    //{
    //    ArgumentNullException.ThrowIfNull(builder);

    //    builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter?.Resource));

    //    return builder;
    //}
}
