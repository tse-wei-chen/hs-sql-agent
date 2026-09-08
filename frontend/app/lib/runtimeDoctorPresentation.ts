import type {
  RuntimeDoctorCheck,
  RuntimeDoctorResponse,
  RuntimeDoctorStatus,
} from "@/api/doctor";

export const doctorStatusClass = (status: RuntimeDoctorStatus) => {
  if (status === "Error")
    return "border-red-300 bg-red-50 text-red-800 dark:border-red-900 dark:bg-red-950/30 dark:text-red-200";
  if (status === "Warning")
    return "border-amber-300 bg-amber-50 text-amber-800 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-200";
  return "border-emerald-300 bg-emerald-50 text-emerald-800 dark:border-emerald-900 dark:bg-emerald-950/30 dark:text-emerald-200";
};

export const doctorSummary = (result: RuntimeDoctorResponse) => ({
  errors: result.checks.filter(check => check.status === "Error").length,
  warnings: result.checks.filter(check => check.status === "Warning").length,
  healthy: result.checks.filter(check => check.status === "Healthy").length,
});

export const groupDoctorChecks = (checks: RuntimeDoctorCheck[]) =>
  checks.reduce<Record<string, RuntimeDoctorCheck[]>>((groups, check) => {
    (groups[check.category] ??= []).push(check);
    return groups;
  }, {});
