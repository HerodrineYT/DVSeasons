"""Prepare the user-supplied field recording for quiet positional ambience."""
import sys, subprocess, wave, array, math
from pathlib import Path
import imageio_ffmpeg

data = subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), '-v', 'error', '-i', sys.argv[1],
    '-f','s16le','-ac','1','-ar','24000','-'], check=True, capture_output=True).stdout
samples = array.array('h', data)
peak = max(abs(s) for s in samples)
rms = math.sqrt(sum(float(s)*s for s in samples)/len(samples))
gain = min(.13*32768/rms, .65*32768/peak)
fade = 2400
for i in range(len(samples)):
    envelope = min(1, i/fade, (len(samples)-1-i)/fade)
    samples[i] = round(samples[i]*gain*envelope)
output = Path(sys.argv[2]); output.parent.mkdir(parents=True,exist_ok=True)
with wave.open(str(output),'wb') as target:
    target.setnchannels(1);target.setsampwidth(2);target.setframerate(24000);target.writeframes(samples.tobytes())
print(f'{output}: {len(samples)/24000:.2f}s, mono PCM16; source content retained, short loop fades.')
