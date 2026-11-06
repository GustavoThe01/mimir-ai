# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e [Versionamento Semântico](https://semver.org/lang/pt-BR/).

## [Não lançado]

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
