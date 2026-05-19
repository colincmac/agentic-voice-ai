using Agents.AI.ContactCenter.IvrWorkflow;
using Showcase.Agent.IntentAgent.Classifiers;
using Showcase.Agent.IntentAgent.Services;
using Showcase.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// gRPC SLM intent-classification service. The showcase backs the wire
// contract with an in-process keyword classifier; the GPU host swap (Phi-4-mini
// behind ONNX runtime / TorchSharp) plugs in behind the same IIntentClassifier
// contract without any wire-protocol change.
// See docs/architecture/aks-topology.md for the GPU node-pool topology and
// KEDA scaling shape on this service.
builder.Services.AddGrpc();
builder.Services.AddSingleton<IIntentClassifier, StubKeywordIntentClassifier>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGrpcService<IntentClassificationGrpcService>();

app.Run();
