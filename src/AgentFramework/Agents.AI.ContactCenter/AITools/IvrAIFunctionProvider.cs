using System;
using System.Collections.Generic;
using System.Text;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.AITools;

public class IvrAIFunctionProvider(IEnumerable<IAIToolCollection> toolCollections) : IAIToolCollection
{
    
    public IEnumerable<AITool> AsAITools()
    {
        return toolCollections.SelectMany(c => c.AsAITools());
    }
}
