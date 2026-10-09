using WavenVoIP;
using WavenVoIP.Services;

int total = 0, falhas = 0;

void Check(string cenario, string esperado, string obtido)
{
    total++;
    var ok = string.Equals(esperado, obtido, StringComparison.Ordinal);
    if (!ok) falhas++;
    Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {cenario} | esperado='{esperado}' obtido='{obtido}'");
}

void CheckBool(string cenario, bool esperado, bool obtido)
{
    total++;
    var ok = esperado == obtido;
    if (!ok) falhas++;
    Console.WriteLine($"[{(ok ? "OK  " : "FAIL")}] {cenario} | esperado={esperado} obtido={obtido}");
}

Console.WriteLine("=== PhoneNumberNormalizer.NormalizeBrazilPhone / NormalizeForDial ===");

// Celular sem 9 -> deve ganhar o 9
Check("Celular sem 9 (10 digitos)", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("6684263277"));
Check("Celular sem 9 via NormalizeForDial", "66984263277", PhoneNumberNormalizer.NormalizeForDial("6684263277"));

// Celular ja com 9 -> permanece igual (idempotencia de entrada)
Check("Celular ja com 9 permanece igual", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("66984263277"));

// Fixo -> NUNCA recebe o 9
Check("Fixo permanece intacto", "6631998716", PhoneNumberNormalizer.NormalizeBrazilPhone("6631998716"));

// Com codigo de pais 55
Check("Celular com 55 sem 9", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("556684263277"));
Check("Celular com 55 e ja com 9", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("5566984263277"));

// Com +55
Check("Celular com +55 sem 9", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("+5566984263277"));

// Pontuacao / espacos / parenteses / hifen
Check("Formatado (66) 8426-3277", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("(66) 8426-3277"));
Check("Formatado com espacos 66 9 8426 3277", "66984263277", PhoneNumberNormalizer.NormalizeBrazilPhone("66 9 8426 3277"));

// Idempotencia: normalizar de novo o resultado ja normalizado nao pode mudar nada
var passo1 = PhoneNumberNormalizer.NormalizeBrazilPhone("6684263277");
var passo2 = PhoneNumberNormalizer.NormalizeBrazilPhone(passo1);
Check("Idempotencia (normalizar 2x = normalizar 1x)", passo1, passo2);

var fixoPasso1 = PhoneNumberNormalizer.NormalizeBrazilPhone("6631998716");
var fixoPasso2 = PhoneNumberNormalizer.NormalizeBrazilPhone(fixoPasso1);
Check("Idempotencia fixo (normalizar 2x = normalizar 1x)", fixoPasso1, fixoPasso2);

Console.WriteLine();
Console.WriteLine("=== DialPlanService.AplicarRegraDeDiscagem (rota aplicada DEPOIS da normalizacao) ===");

Check("Discador: celular sem 9 -> Operadora", "166984263277",
    DialPlanService.AplicarRegraDeDiscagem("6684263277", SaidaChamada.Operadora));

Check("Discador: celular ja com 9 -> Operadora (sem duplicar 9)", "166984263277",
    DialPlanService.AplicarRegraDeDiscagem("66984263277", SaidaChamada.Operadora));

Check("Discador: fixo -> Operadora (nunca ganha 9)", "16631998716",
    DialPlanService.AplicarRegraDeDiscagem("6631998716", SaidaChamada.Operadora));

Check("Discador: celular sem 9 -> WhatsApp TIM", "266984263277",
    DialPlanService.AplicarRegraDeDiscagem("6684263277", SaidaChamada.WhatsAppTim));

Check("Discador: celular sem 9 -> WhatsApp Vivo", "366984263277",
    DialPlanService.AplicarRegraDeDiscagem("6684263277", SaidaChamada.WhatsAppVivo));

// Historico: numero ja veio com prefixo de rota salvo (ex.: rediscagem de uma chamada ja
// registrada apos esta correcao, portanto ja de 12+ digitos com rota+9) -> nao pode duplicar
// rota nem 9. NOTA: rota(1 digito) + celular ANTIGO sem 9 (11 digitos totais, ex.
// "16684263277") e uma ambiguidade PRE-EXISTENTE do DialPlanService.RemoverPrefixoDeRota,
// indistinguivel de um DDD real 16/26/36 legitimo — nao e novidade desta correcao e fica
// fora do escopo aqui (o app so grava numeroFinal com rota apos esta correcao, quando o
// celular ja tem 9 -> 12+ digitos -> sem ambiguidade, ver TemPrefixoDeRota).
Check("Historico: numero ja roteado (rota 1) rediscado -> mesma rota, sem duplicar 9", "166984263277",
    DialPlanService.AplicarRegraDeDiscagem("166984263277", SaidaChamada.Operadora));

// Idempotencia de ponta a ponta: aplicar a regra sobre o proprio resultado (menos a rota) repete o mesmo resultado
var discado1 = DialPlanService.AplicarRegraDeDiscagem("6684263277", SaidaChamada.Operadora);
var semRotaDeVolta = DialPlanService.RemoverPrefixoDeRota(discado1);
var discado2 = DialPlanService.AplicarRegraDeDiscagem(semRotaDeVolta, SaidaChamada.Operadora);
Check("Idempotencia discagem (discar 2x o mesmo numero apos remover rota)", discado1, discado2);

Console.WriteLine();
Console.WriteLine("=== WhatsAppService.NormalizarTelefoneParaEnvio ===");

Check("WhatsApp envio: celular sem 9", "5566984263277",
    WhatsAppService.NormalizarTelefoneParaEnvio("6684263277"));

Check("WhatsApp envio: celular ja com 9", "5566984263277",
    WhatsAppService.NormalizarTelefoneParaEnvio("66984263277"));

Check("WhatsApp envio: fixo permanece sem 9", "556631998716",
    WhatsAppService.NormalizarTelefoneParaEnvio("6631998716"));

Check("WhatsApp envio: numero com prefixo de rota TIM salvo, sem 9", "5566984263277",
    WhatsAppService.NormalizarTelefoneParaEnvio("26684263277"));

Check("WhatsApp envio: numero ja com 55 e sem 9", "5566984263277",
    WhatsAppService.NormalizarTelefoneParaEnvio("556684263277"));

Console.WriteLine();
Console.WriteLine("=== Equivalencia para match de contato (WhatsApp/Historico -> Contato salvo) ===");

CheckBool("Celular sem 9 e com 9 normalizam para a MESMA chave", true,
    PhoneNumberNormalizer.NormalizeBrazilPhone("6684263277") ==
    PhoneNumberNormalizer.NormalizeBrazilPhone("66984263277"));

CheckBool("Celular com 55 e sem 55 (ambos sem 9) normalizam para a MESMA chave", true,
    PhoneNumberNormalizer.NormalizeBrazilPhone("6684263277") ==
    PhoneNumberNormalizer.NormalizeBrazilPhone("556684263277"));

CheckBool("Dois numeros DIFERENTES nao viram equivalentes", false,
    PhoneNumberNormalizer.NormalizeBrazilPhone("6684263277") ==
    PhoneNumberNormalizer.NormalizeBrazilPhone("6684263278"));

// ResolverNomePorNumero: contrato de "sem match" preservado (nao usa storage real de contatos
// alem de leitura; nao escreve nada em disco).
var semMatch = ContatoStorageService.ResolverNomePorNumero("00000000000");
CheckBool("ResolverNomePorNumero sem match devolve o proprio numero de entrada", true,
    semMatch == "00000000000");

Console.WriteLine();
Console.WriteLine("=== DDD: validacao antes da discagem (caso real ramal 104, 09/10/2026) ===");

// Numero correto do cliente: DDD 55 + 99726-8679. Todas as formas legitimas discam igual.
Check("DDD 55, 11 digitos, saida TIM", "255997268679", DialPlanService.AplicarRegraDeDiscagem("55997268679", SaidaChamada.WhatsAppTim));
Check("DDD 55, 11 digitos, saida Operadora", "155997268679", DialPlanService.AplicarRegraDeDiscagem("55997268679", SaidaChamada.Operadora));
Check("DDI 55 + DDD 55 (como chega pelo 0800), saida TIM", "255997268679", DialPlanService.AplicarRegraDeDiscagem("5555997268679", SaidaChamada.WhatsAppTim));
Check("+55 55 99726-8679 formatado, saida TIM", "255997268679", DialPlanService.AplicarRegraDeDiscagem("+55 55 99726-8679", SaidaChamada.WhatsAppTim));
Check("Item do historico com prefixo de rota 1 -> rediscar pela TIM", "255997268679", DialPlanService.AplicarRegraDeDiscagem("155997268679", SaidaChamada.WhatsAppTim));
Check("Item do historico com prefixo de rota 2 -> rediscar pela Operadora", "155997268679", DialPlanService.AplicarRegraDeDiscagem("255997268679", SaidaChamada.Operadora));
Check("DDD 55 sem nono digito ganha o 9", "255997268679", DialPlanService.AplicarRegraDeDiscagem("5597268679", SaidaChamada.WhatsAppTim));
Check("DDI 55 + DDD 55 sem nono digito", "255997268679", DialPlanService.AplicarRegraDeDiscagem("555597268679", SaidaChamada.WhatsAppTim));
Check("DDD 66 (matriz) inalterado", "266984263277", DialPlanService.AplicarRegraDeDiscagem("66984263277", SaidaChamada.WhatsAppTim));
Check("DDI 55 + DDD 66", "166984263277", DialPlanService.AplicarRegraDeDiscagem("5566984263277", SaidaChamada.Operadora));
Check("Fixo DDD 66 (10 digitos) nao ganha 9", "16631998716", DialPlanService.AplicarRegraDeDiscagem("6631998716", SaidaChamada.Operadora));

// Nono digito NAO e' acrescentado quando o DDD nao existe.
Check("DDD 25 inexistente + 8 digitos: NAO completa o 9", "2597268679", PhoneNumberNormalizer.NormalizeBrazilPhone("2597268679"));
Check("DDD 20 inexistente + 8 digitos: NAO completa o 9", "2097268679", PhoneNumberNormalizer.NormalizeBrazilPhone("2097268679"));
Check("DDD 11 valido + 8 digitos celular: completa o 9", "11987654321", PhoneNumberNormalizer.NormalizeBrazilPhone("1187654321"));
Check("Numero ja com 9 nao muda (idempotente)", "25997268679", PhoneNumberNormalizer.NormalizeBrazilPhone("25997268679"));

// Deteccao de DDD inexistente no numero que sera discado.
CheckBool("DDD 55 existe", false, PhoneNumberNormalizer.TemDddInexistente("55997268679"));
CheckBool("DDD 66 existe", false, PhoneNumberNormalizer.TemDddInexistente("66984263277"));
CheckBool("DDD 25 NAO existe (numero digitado errado no 104)", true, PhoneNumberNormalizer.TemDddInexistente("25997268679"));
CheckBool("DDD 25 NAO existe, 10 digitos", true, PhoneNumberNormalizer.TemDddInexistente("2597268679"));
CheckBool("0800 nao e' validado como DDD", false, PhoneNumberNormalizer.TemDddInexistente("08007021200"));
CheckBool("Ramal nao e' validado", false, PhoneNumberNormalizer.TemDddInexistente("104"));
CheckBool("Numero local de 9 digitos (sem DDD) nao e' validado", false, PhoneNumberNormalizer.TemDddInexistente("997268679"));
CheckBool("Internacional longo nao e' validado", false, PhoneNumberNormalizer.TemDddInexistente("14155552671234"));
CheckBool("Fixo de DDD 66 pela rota 2 nao gera alerta falso", false,
    PhoneNumberNormalizer.TemDddInexistente(DialPlanService.AplicarRegraDeDiscagem("6631998716", SaidaChamada.WhatsAppTim).Substring(1)));
CheckBool("Entrada errada do caso real continua discavel, mas e' sinalizada", true,
    PhoneNumberNormalizer.TemDddInexistente(DialPlanService.AplicarRegraDeDiscagem("25997268679", SaidaChamada.WhatsAppTim).Substring(1)));

// Todos os DDDs reais sao aceitos; quantidade conferida com o plano de numeracao (67 DDDs).
var validos = Enumerable.Range(10, 90).Count(d => PhoneNumberNormalizer.DddExiste(d.ToString()));
Check("Quantidade de DDDs validos", "67", validos.ToString());
foreach (var ddd in new[] { "11", "21", "27", "31", "38", "41", "46", "51", "55", "61", "65", "66", "71", "79", "81", "91", "94", "98", "99" })
    CheckBool($"DDD {ddd} existe", true, PhoneNumberNormalizer.DddExiste(ddd));
foreach (var ddd in new[] { "10", "20", "23", "25", "26", "29", "30", "36", "39", "40", "50", "52", "56", "57", "58", "59", "60", "70", "72", "76", "78", "80", "90" })
    CheckBool($"DDD {ddd} NAO existe", false, PhoneNumberNormalizer.DddExiste(ddd));

// Numero mostrado na janela de escolha de rota.
Check("Formatacao celular DDD 55", "(55) 99726-8679", PhoneNumberNormalizer.FormatarNacional("55997268679"));
Check("Formatacao fixo DDD 66", "(66) 3199-8716", PhoneNumberNormalizer.FormatarNacional("6631998716"));
Check("Formatacao do numero errado deixa o DDD visivel", "(25) 99726-8679", PhoneNumberNormalizer.FormatarNacional("25997268679"));
Check("Seletor: historico com rota 1 mostra o nacional", "(55) 99726-8679", PhoneNumberNormalizer.FormatarNacional(DialPlanService.NumeroNacionalParaDiscagem("155997268679")));
Check("Seletor: entrada com DDI mostra o nacional", "(55) 99726-8679", PhoneNumberNormalizer.FormatarNacional(DialPlanService.NumeroNacionalParaDiscagem("5555997268679")));
Check("Seletor: ramal fica como esta", "104", PhoneNumberNormalizer.FormatarNacional(DialPlanService.NumeroNacionalParaDiscagem("104")));

Console.WriteLine();
Console.WriteLine($"=== RESULTADO: {total - falhas}/{total} passaram, {falhas} falha(s) ===");
Environment.Exit(falhas == 0 ? 0 : 1);
