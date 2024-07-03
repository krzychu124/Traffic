import { VanillaComponentsResolver } from "types/internal";
import { PropsWithChildren } from "react";
import { useLocalization } from "cs2/l10n";
import { UIKeys } from "types/traffic";
import { BalloonDirection } from "cs2/ui";

interface Props {
  title: string | null;
  description: string | null;
  direction?: BalloonDirection;
  keyBind?: any;
}

export const DescriptionTooltipWithKeyBind = ({ title, description, direction = "right", keyBind, children }: PropsWithChildren<Props>) => {
  const { DescriptionTooltip, LocalizedInputPath } = VanillaComponentsResolver.instance;
  const { translate } = useLocalization();

  return (
    <DescriptionTooltip title={title}
                        description={description}
                        content={(keyBind?.binding && (
                          <p>
                            {translate(UIKeys.SHORTCUT)}
                            <strong>
                              <LocalizedInputPath
                                group={keyBind.group}
                                binding={keyBind.binding}
                                gamepadType={0}
                                keyboardLayout={0}
                                short={""}
                                modifiers={keyBind.modifiers}
                                layoutMap={""}
                              />
                            </strong>
                          </p>)
                        )}
                        direction={direction}
                        alignment="end"
    >
      <div>{children}</div>
    </DescriptionTooltip>
  )
}