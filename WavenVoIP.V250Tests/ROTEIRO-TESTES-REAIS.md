# WavenVoIP 2.5.0 — roteiro de testes reais (ramais 103, 104 e 109)

Build de teste: `publish-v250-teste\` (ou `WavenVoIP-2.5.0-TESTE.zip`). Não é instalador e não
mexe no Auto Update: copie a pasta para o PC do ramal, feche o WavenVoIP em uso e abra o
`WavenVoIP.exe` da pasta copiada. Ele usa a mesma configuração e o mesmo histórico do PC.
Para voltar, feche e abra o WavenVoIP instalado (2.4.7).

Anote para cada teste: **ramal, data, hora de início e número do outro lado**. É com isso que as
gravações e os logs são localizados depois.

## A. Microfone — nivelamento ligado x desligado (103, 104 e 109)

Em cada ramal, com o mesmo headset e, de preferência, ligando para o mesmo telefone:

1. Nivelamento **ligado** (padrão): 2 chamadas de ~1 minuto, falando normalmente.
2. Feche o WavenVoIP e rode `Definir-MicAutoGain.ps1 -Estado Desligado`. Abra de novo.
3. Nivelamento **desligado**: mais 2 chamadas iguais.
4. Feche, rode `Definir-MicAutoGain.ps1 -Estado Ligado` e abra de novo.

Ouvir nas gravações (Histórico > tocar gravação):
- a voz do operador ficou no mesmo volume da voz do cliente?
- há distorção, voz "estourada" ou cortada?
- o ruído de fundo ou a respiração ficaram altos nas pausas?
- o volume sobe e desce sozinho durante a frase?
- quem está do outro lado ouve a própria voz de volta (eco)? Perguntar ao interlocutor.

Repetir uma chamada de cada modo em um posto que use **caixa de som em vez de headset**, se
houver: é onde o risco de eco é maior.

## B. Conferência

Sala 800. Fazer com os três ramais:

1. **Chamada direta**: 104 liga para um celular; depois adiciona o 103 à conferência e une a
   chamada à sala.
2. **Chamada da fila**: um celular liga para a empresa, cai na fila e o 109 atende; o 109
   adiciona o 104 e une a chamada à sala.
3. **Três participantes**: repetir o item 2 adicionando também um número externo.
4. **Falha preservando a chamada**: se aparecer a mensagem de que não foi possível identificar
   os canais, confirmar que a ligação original continua ativa.

Observar e anotar:
- alguém ouve a própria voz de volta? Quem, e depois de quem entrou na sala?
- algum participante está muito mais alto ou mais baixo que os outros? Qual?
- existe chiado contínuo quando ninguém fala?

Os participantes devem estar em **salas diferentes** ou todos de headset. Dois aparelhos no
mesmo ambiente, um deles em viva-voz, produzem eco real que não é defeito do sistema.

## C. Histórico

1. Filtro de tipos > **Fora do Horário**: conferir a ligação de 11/10 às 16:17 (Paulo Soares).
2. Ligar de um celular, desligar antes de atenderem e ligar de novo em menos de 2 minutos,
   desta vez sendo atendido: devem aparecer **duas linhas** (perdida + recebida) e **nenhum
   popup** de perdida depois do atendimento.
3. Ligar duas vezes seguidas sem ser atendido: duas linhas de perdida, **um único popup**, e o
   contador de perdidas contando as duas.

## Logs que serão analisados

Em cada PC: `%LOCALAPPDATA%\WavenVoIP\sip_signal_debug.log`

| Linha | O que mostra |
|---|---|
| `AUDIO_MUTE_PROXY_CREATED ... mic_auto_gain=` | se o nivelamento estava ligado naquela chamada |
| `AUDIO_TX_AUTO_GAIN` | nível de fala que veio do microfone, ganho médio/máximo, ruído, blocos limitados |
| `CONF AMI JOIN_CANAL` | todos os canais existentes na hora da união |
| `CONF AMI JOIN_SELECAO_EXATA` | os canais levados à sala (esperado: `SIP/<ramal>-…` + tronco ou ramal do outro lado; nunca `Local/…`) |
| `CONF AMI JOIN_SELECAO_IMPOSSIVEL` | união recusada com a chamada preservada |
| `CONF AMI REDIRECT_ATOMICO` | resultado do envio à sala |
| `MISSED_NOTIFY_SUPPRESSED_ANSWERED motivo=retorno_atendido_depois` | perdida sem popup porque o número foi atendido depois |
| `MISSED_SUPPRESSED motivo=popup_recente_mesmo_numero` | segunda perdida do mesmo número sem novo popup |

`cdr_sync.log`: `CDR_SELF_CALL_RETURN` marca a "entrada" que era só o retorno da própria ligação.

Medição das gravações (opcional): `python -I medir-niveis-gravacao.py <pasta com os .wav>` —
coluna `dif` = diferença entre o lado alto e o lado baixo; `clip%` acima de 0,1 = distorção.
