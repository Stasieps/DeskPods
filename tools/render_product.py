from pathlib import Path
import numpy as np, cv2, math, json, hashlib
from PIL import Image, ImageDraw
ROOT=Path(__file__).resolve().parents[1]; ASSETS=ROOT/'src/PodsView/Assets'; OUT=ROOT/'preview'; OUT.mkdir(exist_ok=True)
N=512; SIZE=256; COUNT=61; COLS=8; PERIOD=18.0; MAX_ANGLE=20.0
source=Image.open(ASSETS/'airpods-case-open.png').convert('RGBA')
rgba=np.asarray(source,dtype=np.float32)/255.; h,w=rgba.shape[:2]
u,v=np.meshgrid(np.linspace(0,1,w,dtype=np.float32),np.linspace(0,1,h,dtype=np.float32))
inside=(rgba[:,:,3]>.06).astype(np.uint8)
dist=cv2.distanceTransform(inside,cv2.DIST_L2,cv2.DIST_MASK_PRECISE)
shoulder=np.clip(dist/(min(w,h)*.085),0,1); shoulder=shoulder*shoulder*(3-2*shoulder)
# A restrained, continuous depth estimate: the open lid is behind the buds/front shell.
# It is not a recovered 360-degree model. No unseen surfaces are invented.
t=np.clip((v-(.47-.10*u))/.20,0,1); t=t*t*(3-2*t)
z=(-.11+.34*t)*shoulder
z+=.035*np.exp(-((u-.37)/.14)**2-((v-.49)/.17)**2)*shoulder
z+=.035*np.exp(-((u-.76)/.14)**2-((v-.38)/.17)**2)*shoulder
z=z.astype(np.float32)
premult=rgba.copy(); premult[:,:,:3]*=premult[:,:,3:]
# Orthographic-like long-lens camera keeps the product steady instead of breathing in size.
D=7.0; EXTENT=1.15; aspect=h/w
coords=(np.arange(N,dtype=np.float32)+.5)/N*2*EXTENT-EXTENT
X,Y=np.meshgrid(coords,-coords)
frames=[]
for i,angle in enumerate(np.linspace(-MAX_ANGLE,MAX_ANGLE,COUNT)):
 a=math.radians(float(angle)); c,s=math.cos(a),math.sin(a)
 xx=X.copy(); yy=Y.copy()
 for iteration in range(22):
  mx=((xx+1)*.5*(w-1)).astype(np.float32)
  my=((aspect-yy)/(2*aspect)*(h-1)).astype(np.float32)
  zz=cv2.remap(z,mx,my,cv2.INTER_LINEAR,borderMode=cv2.BORDER_CONSTANT)
  xx=(D*X-zz*(D*s+X*c))/(D*c-X*s)
  yy=Y*(D+s*xx-c*zz)/D
 mx=((xx+1)*.5*(w-1)).astype(np.float32)
 my=((aspect-yy)/(2*aspect)*(h-1)).astype(np.float32)
 sample=cv2.remap(premult,mx,my,cv2.INTER_CUBIC,borderMode=cv2.BORDER_CONSTANT)
 sample=np.clip(sample,0,1)
 # Premultiplied resampling prevents a black/white fringe at the soft alpha silhouette.
 small=cv2.resize(sample,(SIZE,SIZE),interpolation=cv2.INTER_AREA)
 alpha=np.clip(small[:,:,3:4],0,1)
 rgb=np.where(alpha>1e-5,small[:,:,:3]/np.maximum(alpha,1e-5),0)
 final=np.concatenate([np.clip(rgb,0,1),alpha],axis=2)
 frame=Image.fromarray(np.uint8(np.clip(final*255+.5,0,255)),'RGBA')
 frames.append(frame)
 if i%10==0: print('render',i,'angle',round(float(angle),2),flush=True)
rows=math.ceil(COUNT/COLS)
atlas=Image.new('RGBA',(COLS*SIZE,rows*SIZE))
for i,f in enumerate(frames): atlas.paste(f,((i%COLS)*SIZE,(i//COLS)*SIZE))
atlas.save(ASSETS/'airpods-turn-atlas.png',optimize=True)
frames[COUNT//2].save(ASSETS/'airpods-turn-still.png',optimize=True)
manifest={'kind':'photo-depth-turntable','source':'airpods-case-open.png','sourceSha256':hashlib.sha256((ASSETS/'airpods-case-open.png').read_bytes()).hexdigest(),'frameCount':COUNT,'frameSize':SIZE,'columns':COLS,'periodSeconds':PERIOD,'angleDegrees':MAX_ANGLE,'notA360Model':True}
(ASSETS/'airpods-turn.json').write_text(json.dumps(manifest,indent=2))
# Contact sheet is for visual inspection; preview uses the exact atlas frames and timing.
contact=Image.new('RGB',(3*256,2*286),'#08090A')
for slot,i in enumerate([0,15,30,45,60,30]):
 f=frames[i]; tile=Image.new('RGBA',(256,256),'#08090A'); tile.alpha_composite(f)
 contact.paste(tile.convert('RGB'),((slot%3)*256,(slot//3)*286))
 ImageDraw.Draw(contact).text(((slot%3)*256+12,(slot//3)*286+263),f'{-MAX_ANGLE+2*MAX_ANGLE*i/(COUNT-1):+.1f} deg',fill='#9A9CA2')
contact.save(OUT/'contact.png')
preview=[]
FPS=20
for tick in range(round(PERIOD*FPS)):
 t=tick/FPS
 i=round((.5+.5*math.sin(2*math.pi*t/PERIOD))*(COUNT-1))
 tile=Image.new('RGBA',(330,390),'#08090A')
 # Tile scale 3x: 108x128 inner area, square image centered vertically as Stretch=Uniform.
 f=frames[i].resize((324,324),Image.Resampling.LANCZOS)
 tile.alpha_composite(f,(3,33))
 draw=ImageDraw.Draw(tile); draw.rounded_rectangle((1,1,328,388),radius=26,outline='#25272B',width=2)
 preview.append(tile.convert('RGB'))
# A shared palette avoids per-frame palette pumping on the white shell.
palette=contact.quantize(colors=255)
indexed=[f.quantize(palette=palette,dither=Image.Dither.NONE) for f in preview]
indexed[0].save(OUT/'PodsView-motion.gif',save_all=True,append_images=indexed[1:],duration=round(1000/FPS),loop=0,optimize=False)
# Verify alpha border and monotonic geometry coverage instead of judging just a still image.
metrics=[]
for i,f in enumerate(frames):
 ar=np.asarray(f)[:,:,3]
 assert not np.any(ar[[0,-1],:]) and not np.any(ar[:,[0,-1]]),('clipping at atlas boundary',i)
 ys,xs=np.where(ar>128); metrics.append((int(xs.min()),int(xs.max()),int(ys.min()),int(ys.max()),len(xs)))
assert len({hashlib.sha256(f.tobytes()).hexdigest() for f in frames})==COUNT
(OUT/'metrics.json').write_text(json.dumps({'frames':COUNT,'allUnique':True,'transparentBorders':True,'bounds':metrics,'atlasBytes':(ASSETS/'airpods-turn-atlas.png').stat().st_size},indent=2))
print('DONE atlas bytes', (ASSETS/'airpods-turn-atlas.png').stat().st_size, 'GIF bytes', (OUT/'PodsView-motion.gif').stat().st_size,flush=True)
