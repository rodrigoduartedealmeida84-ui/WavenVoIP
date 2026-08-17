using System;
using System.Threading;
using System.Threading.Tasks;

namespace WavenVoIP.Services
{
    /// <summary>
    /// v2.4.4 — instrumentação de tasks fire-and-forget (item 3 do pedido de diagnóstico).
    ///
    /// Problema que isto resolve: hoje, uma task "solta" (ex.: <c>_ = Task.Run(Foo)</c>) que
    /// falha só produz sinal quando o GC coleta o objeto Task e dispara
    /// <see cref="TaskScheduler.UnobservedTaskException"/> — que pode acontecer muito depois
    /// da falha real, e nesse ponto já se perdeu qualquer contexto de "quem" originou aquilo
    /// (ver App.xaml.cs). <see cref="FireAndForget"/> observa a task no exato momento em que
    /// ela falha, loga com o contexto (<paramref name="origem"/>) e NUNCA deixa a exceção se
    /// propagar de volta pro chamador — o comportamento funcional da task em si não muda em
    /// nada, ela roda exatamente igual.
    ///
    /// Efeito colateral desejado: ler <c>t.Exception</c> dentro do ContinueWith marca a task
    /// como "observada" (comportamento padrão do TPL — é o mesmo idioma usado pra suprimir
    /// UnobservedTaskException de propósito). Então uma task que passou por aqui NUNCA mais
    /// dispara o handler global em App.xaml.cs pra essa mesma falha — sem isso, o mesmo erro
    /// viraria dois incidentes (um aqui com CaptureSource=FIRE_AND_FORGET rico em detalhe, outro
    /// depois via TaskScheduler.UnobservedTaskException com CaptureSource=UNOBSERVED_TASK e bem
    /// mais pobre). Nenhuma duplicidade: o mais rico (este) sempre chega primeiro e observa.
    ///
    /// Aplicar SÓ nos pontos catalogados como fire-and-forget de maior risco (NÃO PROTEGIDO ou
    /// PARCIALMENTE PROTEGIDO) — não é pra virar hábito indiscriminado em todo Task.Run novo.
    /// </summary>
    internal static class FireAndForgetExtensions
    {
        public static void FireAndForget(this Task task, string origem)
        {
            task.ContinueWith(t =>
            {
                var ex = t.Exception?.Flatten().InnerException ?? t.Exception;
                if (ex == null) return;
                try { LogHelper.Error($"FIRE_AND_FORGET_FAULTED | origem={origem}", ex); }
                catch { /* telemetria/log nunca pode propagar erro daqui */ }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
