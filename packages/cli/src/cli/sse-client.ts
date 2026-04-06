import http from 'http';

export interface SSECallbacks {
  onEvent: (event: string, data: string) => void;
  onError: (error: Error) => void;
  onClose: () => void;
}

export function connectSSE(url: string, callbacks: SSECallbacks): http.ClientRequest {
  let currentEvent = '';
  let dataBuffer = '';
  let lineBuffer = '';

  const req = http.get(url, (res) => {
    res.on('data', (chunk: Buffer) => {
      const text = chunk.toString('utf-8');
      lineBuffer += text;
      const lines = lineBuffer.split('\n');
      lineBuffer = lines.pop() ?? '';

      for (const line of lines) {
        if (line.startsWith('event:')) {
          currentEvent = line.slice(6).trim();
        } else if (line.startsWith('data:')) {
          const value = line.slice(5).trimStart();
          if (dataBuffer) {
            dataBuffer += '\n' + value;
          } else {
            dataBuffer = value;
          }
        } else if (line === '') {
          if (dataBuffer) {
            callbacks.onEvent(currentEvent || 'message', dataBuffer);
            currentEvent = '';
            dataBuffer = '';
          }
        }
      }
    });

    res.on('end', () => {
      if (lineBuffer) {
        const lines = lineBuffer.split('\n');
        for (const line of lines) {
          if (line.startsWith('event:')) {
            currentEvent = line.slice(6).trim();
          } else if (line.startsWith('data:')) {
            const value = line.slice(5).trimStart();
            if (dataBuffer) {
              dataBuffer += '\n' + value;
            } else {
              dataBuffer = value;
            }
          }
        }
        lineBuffer = '';
      }
      if (dataBuffer) {
        callbacks.onEvent(currentEvent || 'message', dataBuffer);
        currentEvent = '';
        dataBuffer = '';
      }
      callbacks.onClose();
    });

    res.on('error', (err) => {
      callbacks.onError(err);
    });
  });

  req.on('error', (err) => {
    callbacks.onError(err);
  });

  return req;
}
