import { useEffect, useState } from "react";

export type Density = "comfortable" | "compact";

const KEY = "tmq.density";

function read(): Density {
  if (typeof window === "undefined") return "comfortable";
  return (localStorage.getItem(KEY) as Density | null) ?? "comfortable";
}

export function useDensity(): [Density, (d: Density) => void] {
  const [density, setDensity] = useState<Density>(read);

  useEffect(() => {
    document.documentElement.dataset.density = density;
    localStorage.setItem(KEY, density);
  }, [density]);

  return [density, setDensity];
}
