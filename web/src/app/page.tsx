"use client";

import { useEffect } from "react";
import { BrandSplash } from "@/components/progress";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth";

/** Sends each visitor to the right place: sign-in, the platform console or the factory app. */
export default function Home() {
  const { me, loading } = useAuth();
  const router = useRouter();
  useEffect(() => {
    if (loading) return;
    router.replace(!me ? "/login" : me.isSuperAdmin ? "/platform" : "/app");
  }, [me, loading, router]);
  return <BrandSplash />;
}
