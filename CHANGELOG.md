# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e [Versionamento Semântico](https://semver.org/lang/pt-BR/).

## [Não lançado]

### Adicionado
- Base de conhecimento em `knowledge/`: 8 documentos fictícios da Yggdrasil Serviços, com cabeçalho de setor, perfis de acesso e data.
- `DocumentParser`, que valida o cabeçalho e recusa documento sem perfil de acesso.
- `Chunker`: trechos por seção e parágrafo, com sobreposição, citação por arquivo e linhas, caminho da seção e hash do texto.
- Comando `mimir ingerir`, que roda sem chamar o modelo.
- 16 testes novos.
- Índice de trechos no SQLite (`SqliteChunkStore`), com o vetor em BLOB, o hash do texto e o modelo que gerou o vetor.
- `KnowledgeIndexer`, que gera vetores só para os trechos novos ou alterados e refaz tudo quando o modelo muda.
- `SemanticSearch` e o comando `mimir buscar`, com similaridade por cosseno.
- Configuração `Embeddings` separada de `Llm`: Ollama (`paraphrase-multilingual`), OpenAI e o gerador de teste por hashing.
- Opções pagas de embeddings no README, incluindo o motivo de não haver opção Anthropic.
- 10 testes novos.
- Ingestão com o modelo real `paraphrase-multilingual` (768 dimensões): documento certo nas 7 perguntas do teste de fumaça, contra 5 do gerador por hashing. Resultado no README.

## [0.1.0] - 2026-11-08

### Adicionado
- Solution `Mimir.slnx` com Core, Infrastructure, Cli e testes.
- `KnowledgeAssistant`, que depende apenas de `IChatClient`.
- `ChatClientFactory` com os provedores Ollama (padrão), Anthropic, OpenAI e Fake.
- CLI para fazer perguntas pelo terminal.
- 14 testes unitários, sem rede e sem custo.
- Pipeline de CI no GitHub Actions.
- ADR 0001 — provedor configurável; ADR 0002 — fine-tuning em Python.
- Resposta em streaming na CLI, com `KnowledgeAssistant.AskStreamingAsync`.
- Validação com modelo real no Ollama e comparação entre `llama3.2:3b` e `llama3.2:1b` na GTX 1060, com o `llama3.2:3b` mantido como padrão.
- Prompts versionados em arquivos, embutidos no assembly e lidos por `PromptLibrary`.
- `TicketClassifier`: classificação de chamados com saída estruturada, validação por JSON Schema e nova tentativa.
- Comando `mimir classificar` na CLI.
- 12 testes novos, incluindo respostas inválidas e setores inventados.
- Conjunto de avaliação com 20 chamados em JSON Lines (`datasets/classificacao-chamados.v1.jsonl`), 4 por setor.
- `ClassificationEvaluator` e `EvaluationReport`: acurácia, precisão, revocação e F1 por setor, matriz de confusão e lista de erros.
- Comando `mimir avaliar`, que gera o relatório em Markdown.
- Primeira rodada com `llama3.2:3b`: 16 de 20 (80%), registrada em `docs/avaliacao/`.
- 12 testes novos.
- `ResilientChatClient`: limite de tempo por tentativa e nova tentativa com espera crescente, só para falhas passageiras (conexão, tempo esgotado, 429 e 5xx).
- `UsageTrackingChatClient` e `UsageTracker`: tokens de entrada e saída e tempo de cada chamada, no log e no fim da execução da CLI.
- `ChatClientFactory.CreatePipeline`, que monta provedor, resiliência e registro de uso.
- Configurações `Llm:TimeoutSeconds` e `Llm:MaxAttempts`.
- Mensagens de erro com o que fazer quando o provedor não responde.
- 15 testes novos.
