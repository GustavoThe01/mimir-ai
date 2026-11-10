# Roadmap — Mimir (20 dias / 4 semanas)

**Regra do projeto:** nenhuma etapa exige gastar dinheiro. Tudo roda com modelos locais, e as opções pagas ficam documentadas no README.

## Semana 1 — LLM → `v0.1.0`

### Dia 01 — Fundação
- [x] Solution com Core, Infrastructure, Cli e testes
- [x] `ChatClientFactory`: Ollama, Anthropic, OpenAI e provedor de teste
- [x] Chave de API somente por variável de ambiente
- [x] CI sem nenhuma chamada a modelo
- [x] ADR 0001 — Provedor configurável · ADR 0002 — Fine-tuning em Python

### Dia 02 — Modelo real
- [x] Instalar o Ollama e validar a resposta com modelo local
- [x] Escolher o modelo pelo meu hardware e registrar a velocidade medida
- [x] Resposta em streaming na CLI

### Dia 03 — Prompts e saída estruturada
- [x] Prompts versionados em arquivos
- [x] Saída em JSON validada contra um schema

### Dia 04 — Avaliação
- [x] Conjunto de perguntas com resposta esperada
- [x] Comando que roda a avaliação e gera um relatório

### Dia 05 — Resiliência
- [x] Timeout, retry e erros amigáveis
- [x] Registro de tokens e tempo por chamada
- [x] Tag `v0.1.0`

## Semana 2 — Enterprise RAG → `v0.2.0`

### Dia 06 — Ingestão
- [x] Documentos fictícios da empresa
- [x] Divisão em trechos, com metadados de origem

### Dia 07 — Embeddings
- [x] `IEmbeddingGenerator` com modelo local
- [x] Vetores no SQLite
- [x] Documentar as opções pagas de embeddings no README

### Dia 08 — Busca híbrida
- [ ] FTS5 + vetores, com fusão dos rankings
- [ ] Comparar a busca híbrida com cada busca isolada

### Dia 09 — Respostas com citação
- [ ] Prompt com os trechos encontrados e citação obrigatória
- [ ] Responder "não sei" quando não houver fonte

### Dia 10 — Permissões e qualidade
- [ ] Filtro por perfil antes de montar o prompt
- [ ] Teste provando que documento restrito não chega ao modelo
- [ ] Avaliação do RAG com resultado no README; tag `v0.2.0`

## Semana 3 — Fine-Tuning → `v0.3.0`

### Dia 11 — Dados
- [ ] Chamados fictícios rotulados por setor, separados em treino, validação e teste

### Dia 12 — Linha de base
- [ ] Classificar com o modelo sem ajuste e medir acurácia e F1

### Dia 13 — Treino
- [ ] Script em `training/`, rodando em GPU local ou no Google Colab gratuito

### Dia 14 — Modelo no Ollama
- [ ] Exportar o resultado e carregá-lo no Ollama
- [ ] Usar o modelo ajustado pelo C#, sem mudar o núcleo

### Dia 15 — Comparação
- [ ] Relatório do modelo ajustado contra a linha de base
- [ ] Documentar o fine-tuning pago no README; tag `v0.3.0`

## Semana 4 — MCP e Agentes Autônomos → `v1.0.0`

### Dia 16 — Servidor MCP
- [ ] Ferramentas `search_knowledge`, `get_ticket` e `create_ticket`

### Dia 17 — Segurança do MCP
- [ ] Permissões aplicadas às ferramentas
- [ ] Testes de integração com um cliente MCP

### Dia 18 — Agente
- [ ] Laço do agente usando as ferramentas MCP
- [ ] Limite de passos e registro de cada decisão

### Dia 19 — Limites do agente
- [ ] Aprovação humana nas ações que alteram dados
- [ ] Testes de prompt injection

### Dia 20 — Entrega
- [ ] Demonstração de ponta a ponta, README final e tag `v1.0.0`
