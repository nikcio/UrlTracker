import variableResource, {
  IVariableResource,
} from "./variableresource.service";
import { IUrlTrackerVariables } from "./versionproviderservice.constants";

export interface IVersionProvider {
  get version(): string;
}

export class VersionProvider implements IVersionProvider {
  constructor(private variableResource: IVariableResource) {}
  get version(): string {
    const version = this.variableResource.get<string>("version");
    return version;
  }
}

export default new VersionProvider(variableResource);
