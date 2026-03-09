sequenceDiagram
    participant Caller
    participant Router as ISessionRouter
    participant VoiceAI as RealtimeVoiceAgentTransport
    participant Analyzer as ConversationAnalysisTransport
    participant Pipeline as IAudioAnalysisPipeline
    participant TextAnalyzer as ITextSentimentAnalyzer
    participant Correlator as CrossSignalCorrelator
    participant Bus as HubSessionEventBus

    Caller->>Router: audio frames
    Router->>VoiceAI: audio frames (primary)
    Router->>Analyzer: audio frames (parallel)

    VoiceAI->>Bus: Transcript event
    Bus->>Analyzer: Transcript event

    Analyzer->>TextAnalyzer: "I'm fine, thanks"
    TextAnalyzer-->>Analyzer: sentiment = +0.6

    Analyzer->>Correlator: RecordTextSentiment(+0.6)

    Note over Analyzer: 3s window elapsed

    Analyzer->>Pipeline: AnalyzeAsync(buffered audio)
    Pipeline-->>Analyzer: emotion="frustrated", valence=-0.5

    Analyzer->>Correlator: RecordAudioEmotion(frustrated, -0.5)
    Analyzer->>Correlator: Evaluate()
    Correlator-->>Analyzer: divergence=1.1, isDivergent=true

    Analyzer->>Bus: AgentInsight (ConversationSignalAnalysis)
    Bus->>VoiceAI: cross-signal divergence alert
