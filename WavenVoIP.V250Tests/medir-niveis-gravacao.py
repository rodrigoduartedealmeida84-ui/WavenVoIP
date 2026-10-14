# Mede, em gravacoes WAV de chamadas (mono, misturadas pelo Asterisk), os dois niveis de fala:
# "alto" e "baixo" (dBFS) e a diferenca entre eles, alem de pico e clipping.
# Uso: python -I medir-niveis-gravacao.py <pasta com os .wav>   (requer numpy)
# Leitura: dif <= ~6 dB = lados equilibrados; pico perto de 0 com clip% > 0,1 = distorcao.
import sys,os,wave,math
import numpy as np
def db(x): return 20*math.log10(max(x,1e-9))
print(f"{'arquivo':<22}{'fmt':<12}{'pico':>6}{'clip%':>7}{'rms_at':>8}{'baixo':>8}{'alto':>8}{'dif':>6}{'%baixo':>8}{'p10':>7}{'p50':>7}{'p90':>7}")
for fn in sorted(os.listdir(sys.argv[1])):
    w=wave.open(os.path.join(sys.argv[1],fn),'rb')
    ch,sw,fr,n=w.getnchannels(),w.getsampwidth(),w.getframerate(),w.getnframes()
    x=np.frombuffer(w.readframes(n),dtype='<i2').astype(np.float64)/32768.0
    if ch>1: x=x.reshape(-1,ch).mean(axis=1)
    F=int(fr*0.02); m=len(x)//F
    fr_rms=np.sqrt((x[:m*F].reshape(m,F)**2).mean(axis=1)+1e-12)
    d=20*np.log10(fr_rms)
    act=d>-45
    # rajadas de fala: sequencias ativas com tolerancia de 200 ms
    spurts=[];i=0
    while i<m:
        if act[i]:
            j=i;gap=0;last=i
            while j<m and gap<10:
                if act[j]: last=j;gap=0
                else: gap+=1
                j+=1
            seg=fr_rms[i:last+1][act[i:last+1]]
            if len(seg)>=15: spurts.append((i,len(seg),20*math.log10(math.sqrt((seg**2).mean()))))
            i=last+1
        else: i+=1
    lv=np.array([s[2] for s in spurts]); wt=np.array([s[1] for s in spurts],dtype=float)
    # 2-medias ponderado
    c=[np.percentile(lv,20),np.percentile(lv,80)] if len(lv)>3 else [lv.mean(),lv.mean()] if len(lv) else [0,0]
    for _ in range(30):
        a=np.abs(lv-c[0])<=np.abs(lv-c[1])
        if a.sum()==0 or (~a).sum()==0: break
        c=[np.average(lv[a],weights=wt[a]),np.average(lv[~a],weights=wt[~a])]
    a=np.abs(lv-c[0])<=np.abs(lv-c[1]) if len(lv) else np.array([])
    share=wt[a].sum()/wt.sum()*100 if len(lv) else 0
    da=d[act]
    nome=fn.split('_force')[0]
    print(f"{nome:<22}{f'{ch}c/{fr}/{sw*8}b':<12}{db(np.abs(x).max()):>6.1f}{(np.abs(x)>0.98).mean()*100:>7.3f}{db(math.sqrt((x[:m*F].reshape(m,F)[act]**2).mean())):>8.1f}{c[0]:>8.1f}{c[1]:>8.1f}{c[1]-c[0]:>6.1f}{share:>8.0f}{np.percentile(da,10):>7.1f}{np.percentile(da,50):>7.1f}{np.percentile(da,90):>7.1f}")
