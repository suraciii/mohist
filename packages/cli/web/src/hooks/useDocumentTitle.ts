import { useEffect, useRef } from 'react';

export function useDocumentTitle(title: string, active?: boolean) {
  const previousTitle = useRef(document.title);

  useEffect(() => {
    document.title = (active ? '● ' : '') + title;
  }, [title, active]);

  useEffect(() => {
    const restore = previousTitle.current;
    return () => {
      document.title = restore;
    };
  }, []);
}
