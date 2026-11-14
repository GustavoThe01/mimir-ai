"""Gera o conjunto de chamados fictícios rotulados por setor, para o fine-tuning da semana 3.

Sem dependências e determinístico: rodar de novo produz exatamente os mesmos arquivos.

    python training/dados/gerar_chamados.py

Cada SITUAÇÃO é um problema escrito à mão. Cada situação vira 4 chamados, com aberturas, fechamentos e um
detalhe diferentes, como pessoas diferentes escreveriam a mesma coisa.

A divisão em treino, validação e teste é feita por SITUAÇÃO, e não por chamado: as 4 versões de uma situação caem
sempre no mesmo conjunto. Se fosse por chamado, o modelo veria no treino uma redação quase igual à do teste, e a
acurácia do teste mediria memória, não generalização.
"""

import json
import random
from pathlib import Path

SEED = 2026
VARIANTS_PER_SITUATION = 4
# Por setor: 14 situações para treino, 3 para validação e 3 para teste.
SPLIT = {"treino": 14, "validacao": 3, "teste": 3}

SITUATIONS = {
    "FINANCEIRO": [
        "a fatura do fornecedor de limpeza chegou com o valor em dobro",
        "o reembolso da hospedagem da viagem a Recife ainda não caiu na minha conta",
        "preciso emitir a nota fiscal do serviço prestado ao cliente Atlas",
        "o boleto do aluguel do galpão vence amanhã e não está aprovado",
        "o adiantamento de viagem foi depositado com valor menor que o pedido",
        "qual centro de custo uso para a compra das cadeiras do time de dados?",
        "o cliente Orion diz que pagou, mas a fatura dele aparece em aberto",
        "preciso do comprovante de pagamento do fornecedor de software para a auditoria",
        "o cartão corporativo foi recusado na compra das passagens",
        "a cobrança do plano de internet veio duplicada este mês",
        "quero saber o prazo para lançar as despesas do mês no sistema financeiro",
        "a nota de débito do fornecedor de transporte veio sem o número do pedido",
        "preciso cancelar um pagamento agendado por engano para sexta",
        "o orçamento do departamento de marketing estourou e preciso de aprovação",
        "o recibo do almoço com o cliente sumiu, como faço para pedir o reembolso?",
        "o fornecedor de café pede a atualização dos dados bancários para o próximo pagamento",
        "a conciliação bancária de outubro tem uma diferença de R$ 312,40",
        "a multa por atraso do boleto de energia precisa ser lançada em qual conta?",
        "o imposto retido na nota do consultor parece calculado errado",
        "preciso de uma previsão de caixa para a reunião da diretoria",
    ],
    "TI": [
        "a impressora do financeiro não aparece na rede",
        "o monitor externo não é reconhecido pelo notebook novo",
        "o VPN desconecta toda vez que abro o sistema de vendas",
        "recebi um e-mail suspeito pedindo a minha senha do banco",
        "o teclado do meu computador está com várias teclas falhando",
        "preciso instalar o software de edição de vídeo para a campanha",
        "o sistema de chamados está fora do ar desde as nove horas",
        "minha conta foi bloqueada depois de errar a senha",
        "o aplicativo autenticador não gera mais os códigos de acesso",
        "o wi-fi da sala de treinamento não conecta nos celulares",
        "o computador reinicia sozinho no meio das reuniões",
        "preciso de acesso de leitura à pasta compartilhada de contratos",
        "o e-mail não sincroniza no celular depois da troca de senha",
        "o projetor da sala grande não recebe o sinal do notebook",
        "a planilha de metas trava sempre que abro com mais de uma aba",
        "um colaborador saiu da empresa e o acesso dele precisa ser removido",
        "o backup da máquina do laboratório falhou duas noites seguidas",
        "o microfone não funciona nas chamadas de vídeo",
        "o certificado do site interno expirou e o navegador bloqueia a página",
        "o mouse sem fio parou de responder mesmo com pilha nova",
    ],
    "RH": [
        "quero vender dez dias das minhas férias",
        "o vale-refeição deste mês não foi creditado",
        "como funciona a licença-paternidade na empresa?",
        "meu holerite veio com o desconto do plano odontológico em dobro",
        "preciso de uma declaração de vínculo para alugar um apartamento",
        "quero mudar o meu horário de trabalho para entrar mais cedo",
        "vou me casar e quero saber quantos dias de folga tenho",
        "preciso atualizar o meu endereço e a minha conta para o salário",
        "a avaliação de desempenho do meu time ainda não foi aberta no sistema",
        "quero indicar um amigo para a vaga de desenvolvedor",
        "o banco de horas mostra menos horas do que eu fiz",
        "como peço o auxílio para o curso de pós-graduação?",
        "o novo estagiário começa na segunda e ainda não tem crachá nem cadastro",
        "quero incluir meu marido como dependente no plano de saúde",
        "o décimo terceiro veio com um valor diferente do que eu esperava",
        "preciso de um atestado médico registrado para a falta de ontem",
        "como funciona o programa de mentoria para novos líderes?",
        "quero pedir demissão e saber como fica o aviso prévio",
        "a vaga de analista de suporte pode ser divulgada nas redes sociais?",
        "quero entender a política de trabalho remoto para quem mora em outra cidade",
    ],
    "JURIDICO": [
        "o fornecedor quer mudar a cláusula de multa do contrato de manutenção",
        "recebemos um pedido de exclusão de dados pessoais de um ex-cliente",
        "um concorrente está usando uma marca muito parecida com a nossa",
        "preciso de um modelo de acordo de confidencialidade para um parceiro",
        "chegou uma notificação do Procon sobre a reclamação de um cliente",
        "o contrato de aluguel do escritório vence e precisamos renegociar",
        "podemos gravar as ligações do atendimento sem avisar o cliente?",
        "um ex-funcionário entrou com ação pedindo horas extras",
        "o cliente Vega quer incluir uma cláusula de exclusividade no contrato",
        "preciso registrar o nome do nosso novo produto como marca",
        "o termo de uso do aplicativo precisa ser atualizado por causa da LGPD",
        "a prefeitura mandou uma intimação sobre o alvará da loja",
        "um parceiro quer usar o nosso logotipo em um evento sem contrato",
        "precisamos responder a um pedido de informação de um órgão regulador",
        "o fornecedor descumpriu o prazo de entrega e queremos rescindir o contrato",
        "um cliente ameaça processar a empresa por cobrança indevida",
        "podemos usar fotos de banco de imagens gratuito na campanha publicitária?",
        "preciso revisar o contrato de prestação de serviços do consultor externo",
        "recebemos uma carta de cobrança de direitos autorais de uma música do vídeo",
        "o sócio de um fornecedor pediu uma procuração para assinar por ele",
    ],
    "OUTROS": [
        "a lâmpada do corredor do segundo andar está piscando",
        "a geladeira da copa está com cheiro ruim",
        "quero reservar a sala grande para o treinamento de quinta",
        "a vaga de estacionamento de visitantes está sempre ocupada",
        "a porta do banheiro feminino não está trancando",
        "sugiro uma campanha de doação de agasalhos neste inverno",
        "o bebedouro do terceiro andar está sem água gelada",
        "quero organizar um campeonato de pingue-pongue entre os times",
        "a cadeira da recepção está com o encosto quebrado",
        "o elevador de serviço está fazendo um barulho estranho",
        "faltou papel toalha nos banheiros do térreo",
        "quero saber o cardápio do almoço de confraternização",
        "a planta da recepção está morrendo, alguém pode cuidar?",
        "o barulho da obra ao lado atrapalha as reuniões da manhã",
        "a máquina de café expresso está vazando",
        "sugiro trocar o horário da ginástica laboral para depois do almoço",
        "a persiana da sala de reunião pequena não abre",
        "perdi um guarda-chuva preto no auditório",
        "quero propor um clube do livro aberto para todos os times",
        "o ar-condicionado da sala de treinamento está muito frio",
    ],
}

OPENINGS = ["", "Bom dia, ", "Olá, equipe. ", "Oi, pessoal. ", "Boa tarde. ", "Preciso de ajuda: ", "Olá! "]
CLOSINGS = ["", " Obrigado.", " Obrigada!", " É urgente.", " Podem ver hoje?", " Aguardo retorno.", " Fico no aguardo."]
CONTEXTS = ["", " Isso começou ontem.", " Já é a segunda vez esta semana.", " Estou no escritório de Fortaleza.",
            " Trabalho no time comercial.", " Estou em home office.", " O meu gestor pediu para eu abrir o chamado."]


def capitalize(text: str) -> str:
    return text[0].upper() + text[1:] if text else text


def render(situation: str, rng: random.Random) -> str:
    opening = rng.choice(OPENINGS)
    # Depois de "Bom dia, " ou "Preciso de ajuda: " a frase continua em minúscula; nos outros casos, começa uma frase.
    body = situation if opening.endswith((", ", ": ")) else capitalize(situation)
    sentence = body if body.endswith("?") else body + "."
    return f"{opening}{sentence}{rng.choice(CONTEXTS)}{rng.choice(CLOSINGS)}".strip()


def generate():
    rng = random.Random(SEED)
    splits = {name: [] for name in SPLIT}
    for department, situations in SITUATIONS.items():
        assert len(situations) == sum(SPLIT.values()), department
        order = list(range(len(situations)))
        rng.shuffle(order)
        start = 0
        for split, size in SPLIT.items():
            for index in order[start:start + size]:
                seen = set()
                while len(seen) < VARIANTS_PER_SITUATION:
                    seen.add(render(situations[index], rng))
                for variant, text in enumerate(sorted(seen), start=1):
                    group = f"{department.lower()[:3]}-{index + 1:02d}"
                    splits[split].append({"id": f"{group}-{variant}", "grupo": group, "texto": text, "setor": department})
            start += size

    for rows in splits.values():
        rng.shuffle(rows)
    return splits


def main():
    out = Path(__file__).resolve().parents[2] / "datasets" / "chamados"
    out.mkdir(parents=True, exist_ok=True)
    for name, rows in generate().items():
        with open(out / f"{name}.jsonl", "w", encoding="utf-8", newline="\n") as file:
            for row in rows:
                file.write(json.dumps(row, ensure_ascii=False) + "\n")
        print(f"{name}: {len(rows)} chamados")


if __name__ == "__main__":
    main()
