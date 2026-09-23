import json,hashlib,bisect,collections,sys
from pathlib import Path
from PIL import Image
root=Path(sys.argv[1]); read=lambda f:json.loads((root/f).read_text(encoding='utf-8-sig'))
copy_boundary='--screen-copy' in sys.argv[2:]
plan=read('plan.json');freq=plan['frequency'];trace=[json.loads(l) for l in (root/'app-trace.jsonl').read_text(encoding='utf-8-sig').splitlines()];driver=[json.loads(l) for l in (root/'driver.jsonl').read_text().splitlines()];caps=read('scroll-captures.json');inputs=[x['detail'] for x in driver if x['kind']=='scroll-input'];commits=[s['End'] for e in trace if e['kind']=='core-checkpoint-trace' for s in e['data']['samples'] if s['Kind']=='checkpoint-commits'];last=max((c for c in commits if c<=inputs[-1]['begin']),default=0)
for c in caps:
 if copy_boundary:
  assert c['begin'] <= c['copyBegin'] <= c['copyEnd'] <= c['end'], c
  c['begin'],c['end']=c['copyBegin'],c['copyEnd']
 with Image.open(root/c['path']) as im:
  c['vertical']=hashlib.sha256(im.crop((0,0,50,im.height-22)).tobytes()).hexdigest();c['horizontal']=hashlib.sha256(im.crop((550,0,im.width-30,im.height-22)).tobytes()).hexdigest()
ends=[c['end'] for c in caps];records=[];visited={'vertical':{0.0},'horizontal':{0.0}}
for i,cmd in enumerate(inputs):
 a=cmd['begin'];b=inputs[i+1]['begin'] if i+1<len(inputs) else caps[-1]['end'];axis=cmd['axis'];offsetKey=axis+'Offset';beforeIndex=bisect.bisect_right(ends,a)-1
 if beforeIndex<0:
  wheels=[e for e in trace if e['kind']=='sheet-wheel' and a<=e['ticks']<b and e['data']['horizontal']==(axis=='horizontal')]
  target=wheels[0]['data']['wheelHorizontal' if axis=='horizontal' else 'wheelVertical'] if wheels else None
  if target is not None:visited[axis].add(round(target,2))
  records.append(dict(index=cmd['index'],begin=a,end=b,axis=axis,requestedOffset=target,postSave=a>=last,classification='missing-before'));continue
 before=caps[beforeIndex];after=[c for c in caps[beforeIndex+1:] if a<=c['begin']<b];change=next((c for c in after if c[axis]!=before[axis]),None);unchanged=[c for c in after if change is None or c['begin']<change['begin']];lower=(unchanged[-1]['begin']-a)*1000/freq if unchanged else 0;upper=(change['end']-a)*1000/freq if change else None
 wheels=[e for e in trace if e['kind']=='sheet-wheel' and a<=e['ticks']<b and e['data']['horizontal']==(axis=='horizontal')];prior=[e for e in trace if e['kind']=='scroll-view-changed' and e['ticks']<a];prev=prior[-1]['data']['state'][offsetKey] if prior else 0;target=wheels[0]['data']['wheelHorizontal' if axis=='horizontal' else 'wheelVertical'] if wheels else None;views=[e for e in trace if e['kind']=='scroll-view-changed' and a<=e['ticks']<b];matching=[e for e in views if target is not None and abs(e['data']['state'][offsetKey]-target)<1];viewMs=(matching[0]['ticks']-a)*1000/freq if matching else None;returned=target is not None and round(target,2) in visited[axis]
 if target is not None:visited[axis].add(round(target,2))
 noop=target is not None and abs(target-prev)<1
 if noop:label='clamped-or-no-op'
 elif lower>=100 and viewMs is not None and viewMs>=100:label='corroborated-app-delay'
 elif lower>=100:label='visible-delay-attribution-incomplete'
 elif upper is not None and upper<=100:label='changed-by-100ms'
 elif change is None:label='no-changed-capture'
 else:label='capture-bracket-crosses-100ms'
 records.append(dict(index=cmd['index'],begin=a,end=b,axis=axis,priorOffset=prev,requestedOffset=target,requestedReturn=returned,postSave=a>=last,wheelMs=(wheels[0]['ticks']-a)*1000/freq if wheels else None,viewportMs=viewMs,unchangedThroughMs=lower,changedByMs=upper,before=before['path'],lastUnchanged=unchanged[-1]['path'] if unchanged else None,firstChanged=change['path'] if change else None,classification=label))
summary=dict(run=root.parent.name,source=plan['source'],condition=plan['condition'],lastCommit=last,counts=collections.Counter(c['classification'] for c in records),postSaveCounts=collections.Counter(c['classification'] for c in records if c.get('postSave')),commands=records,caveat='ROI hashes bracket visible changes; exact scanout is not measured. Requested return does not establish reuse. Unmatched, no-op, missing and threshold-straddling captures remain separate; command populations overlap callback intervals.')
summary['observationBoundary']='completed BitBlt/GdiFlush screen copy' if copy_boundary else 'whole GDI capture call including bitmap conversion and cleanup'
name='native-command-classification-screen-copy.json' if copy_boundary else 'native-command-classification.json'
(root/name).write_text(json.dumps(summary,indent=2));print(json.dumps({k:v for k,v in summary.items() if k not in ['commands','caveat']}))

