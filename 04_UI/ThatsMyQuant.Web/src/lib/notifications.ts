export interface Notification {
  id: string;
  type: "info" | "warning" | "error" | "success";
  title: string;
  body: string;
  time: Date;
  read: boolean;
}

const listeners: Array<(n: Notification) => void> = [];

export function pushNotification(n: Omit<Notification, "id" | "time" | "read">) {
  const full: Notification = {
    ...n,
    id: crypto.randomUUID(),
    time: new Date(),
    read: false,
  };
  listeners.forEach((fn) => fn(full));
}

export function subscribeToNotifications(fn: (n: Notification) => void): () => void {
  listeners.push(fn);
  return () => {
    const idx = listeners.indexOf(fn);
    if (idx >= 0) listeners.splice(idx, 1);
  };
}
