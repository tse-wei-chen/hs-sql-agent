import { xiorInstanceToken } from "./xiorInstance";

export type RuntimeDoctorStatus = "Healthy" | "Warning" | "Error";

export interface RuntimeDoctorCheck {
  id: string;
  category: string;
  status: RuntimeDoctorStatus;
  title: string;
  detail: string;
  action?: string | null;
}

export interface RuntimeDoctorResponse {
  overallStatus: RuntimeDoctorStatus;
  environment: string;
  deploymentMode: "SingleNode" | "Distributed" | "Mixed" | string;
  generatedAtUtc: string;
  checks: RuntimeDoctorCheck[];
}

export const getRuntimeDoctor = async (): Promise<RuntimeDoctorResponse> => {
  const response = await xiorInstanceToken.get<RuntimeDoctorResponse>(
    "/runtime/operability/doctor",
  );
  return response.data;
};
