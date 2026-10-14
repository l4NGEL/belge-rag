using BelgeRag.Infrastructure;
using BelgeRag.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddBelgeRag(builder.Configuration, "belge-rag-worker");
builder.Services.AddHostedService<IngestConsumer>();

var host = builder.Build();
host.Run();
