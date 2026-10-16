"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { api } from "@/lib/api";
import { useLive } from "@/lib/live";
import { statusTone } from "@/lib/types";
import { Badge, Card, ErrorNote, LiveBadge, PageHeader, errorText } from "@/components/ui";

type FloorAsset = {
  assetId: string; areaName: string | null; code: string; name: string; assetTypeCode: string; category: string; status: keyof typeof statusTone;
  layoutX: number | null; layoutY: number | null; powerTagId: string | null; powerKw: number | null;
};

const colours: Record<string, number> = { ok: 0x16a34a, info: 0x2563eb, warn: 0xd97706, bad: 0xdc2626, muted: 0x6b7280 };
const heights: Record<string, number> = { Production: 3.2, Robotics: 2.4, Energy: 2, Utility: 2.8, Logistics: 1.2, Safety: 1, Sensing: 0.8, Control: 1.6, Vision: 1 };

/**
 * 3D digital twin: the floor plan as a scene. Machines are boxes coloured by their live state, with height by category and a glow
 * that grows with power draw. Drag to orbit, scroll to zoom, click a machine to open it.
 */
export default function TwinPage() {
  const router = useRouter();
  const mount = useRef<HTMLDivElement>(null);
  const meshes = useRef(new Map<string, THREE.Mesh>());
  const [assets, setAssets] = useState<FloorAsset[]>([]);
  const [hover, setHover] = useState<FloorAsset | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { api<FloorAsset[]>("/api/v1/plant/floor").then(setAssets).catch((e) => setError(errorText(e))); }, []);

  const hub = useLive({
    status: (msgs) => setAssets((cur) => cur.map((a) => { const m = msgs.find((x) => x.assetId === a.assetId); return m ? { ...a, status: m.status as FloorAsset["status"] } : a; })),
    values: (msgs) => {
      const byTag = new Map(msgs.map((m) => [m.tagId, m.value]));
      setAssets((cur) => cur.map((a) => (a.powerTagId && byTag.has(a.powerTagId) ? { ...a, powerKw: byTag.get(a.powerTagId)! } : a)));
    },
  });

  // build the scene once, when the assets first arrive
  const built = assets.length > 0;
  useEffect(() => {
    if (!built || !mount.current) return;
    const el = mount.current;
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(getComputedStyle(document.documentElement).getPropertyValue("--panel-2").trim() || "#f1f5f9");
    const camera = new THREE.PerspectiveCamera(45, el.clientWidth / el.clientHeight, 0.1, 1000);
    camera.position.set(0, 70, 75);
    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(el.clientWidth, el.clientHeight);
    el.appendChild(renderer.domElement);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.maxPolarAngle = Math.PI / 2.1;

    scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
    const sun = new THREE.DirectionalLight(0xffffff, 1.4);
    sun.position.set(30, 60, 20);
    scene.add(sun);
    const floor = new THREE.Mesh(new THREE.PlaneGeometry(110, 110), new THREE.MeshStandardMaterial({ color: 0xd1d5db }));
    floor.rotation.x = -Math.PI / 2;
    scene.add(floor);
    scene.add(new THREE.GridHelper(110, 22, 0x9ca3af, 0xc7ccd4));

    // the same automatic layout as the 2D floor plan when no position is saved: areas side by side, machines in rows
    const areas = [...new Set(assets.map((a) => a.areaName ?? "Plant"))];
    const cols = Math.ceil(Math.sqrt(areas.length));
    assets.forEach((a) => {
      let x: number, z: number;
      if (a.layoutX !== null && a.layoutY !== null) {
        x = a.layoutX / 10 - 50; z = a.layoutY / 10 - 50;
      } else {
        const zi = areas.indexOf(a.areaName ?? "Plant");
        const inZone = assets.filter((b) => (b.areaName ?? "Plant") === (a.areaName ?? "Plant"));
        const i = inZone.indexOf(a);
        const zoneW = 100 / cols;
        const perRow = Math.max(1, Math.floor(zoneW / 7));
        x = -50 + (zi % cols) * zoneW + 4 + (i % perRow) * 7;
        z = -50 + Math.floor(zi / cols) * (100 / Math.ceil(areas.length / cols)) + 6 + Math.floor(i / perRow) * 7;
      }
      const h = heights[a.category] ?? 1.5;
      const mesh = new THREE.Mesh(new THREE.BoxGeometry(4.2, h, 3.2), new THREE.MeshStandardMaterial({ color: colours[statusTone[a.status] ?? "muted"], roughness: 0.6 }));
      mesh.position.set(x, h / 2, z);
      mesh.userData = { assetId: a.assetId };
      scene.add(mesh);
      meshes.current.set(a.assetId, mesh);
    });

    const ray = new THREE.Raycaster();
    const mouse = new THREE.Vector2();
    const pick = (e: MouseEvent) => {
      const r = renderer.domElement.getBoundingClientRect();
      mouse.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
      ray.setFromCamera(mouse, camera);
      return ray.intersectObjects([...meshes.current.values()])[0]?.object.userData.assetId as string | undefined;
    };
    const onMove = (e: MouseEvent) => { const id = pick(e); setHover(id ? assetsRef.current.find((a) => a.assetId === id) ?? null : null); renderer.domElement.style.cursor = id ? "pointer" : "grab"; };
    const onClick = (e: MouseEvent) => { const id = pick(e); if (id) router.push(`/app/plant/assets/${id}`); };
    renderer.domElement.addEventListener("mousemove", onMove);
    renderer.domElement.addEventListener("click", onClick);
    const onResize = () => { camera.aspect = el.clientWidth / el.clientHeight; camera.updateProjectionMatrix(); renderer.setSize(el.clientWidth, el.clientHeight); };
    window.addEventListener("resize", onResize);
    let frame = 0;
    const tick = () => { controls.update(); renderer.render(scene, camera); frame = requestAnimationFrame(tick); };
    tick();
    const store = meshes.current;
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener("resize", onResize);
      renderer.domElement.removeEventListener("mousemove", onMove);
      renderer.domElement.removeEventListener("click", onClick);
      controls.dispose();
      renderer.dispose();
      el.removeChild(renderer.domElement);
      store.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [built]);

  // keep the scene in step with live states and power
  const assetsRef = useRef(assets);
  assetsRef.current = assets;
  useEffect(() => {
    assets.forEach((a) => {
      const m = meshes.current.get(a.assetId);
      if (!m) return;
      const mat = m.material as THREE.MeshStandardMaterial;
      mat.color.setHex(colours[statusTone[a.status] ?? "muted"]);
      mat.emissive.setHex(colours[statusTone[a.status] ?? "muted"]);
      mat.emissiveIntensity = a.status === "Running" ? Math.min(0.5, (a.powerKw ?? 0) / 150) : a.status === "Fault" ? 0.6 : 0;
    });
  }, [assets]);

  const counts = assets.reduce<Record<string, number>>((m, a) => ({ ...m, [a.status]: (m[a.status] ?? 0) + 1 }), {});
  return (
    <>
      <PageHeader title="3D digital twin" sub="Drag to orbit, scroll to zoom, click a machine to open it. Colours follow the live state; running machines glow with their power draw."
        actions={<LiveBadge state={hub} />} />
      <ErrorNote error={error} />
      <Card>
        <div className="mb-3 flex flex-wrap gap-2 text-xs">
          {Object.entries(counts).map(([s, n]) => <Badge key={s} tone={statusTone[s as keyof typeof statusTone] ?? "muted"}>{s}: {n}</Badge>)}
          {hover && <span className="ml-auto text-sm"><b>{hover.name}</b> · {hover.status}{hover.powerKw !== null ? ` · ${hover.powerKw.toFixed(1)} kW` : ""}</span>}
        </div>
        <div ref={mount} className="h-[65vh] w-full overflow-hidden rounded-lg" role="img" aria-label="3D view of the factory" data-assets={assets.length} />
      </Card>
    </>
  );
}
