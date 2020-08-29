import React from 'react';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import TagsConnector from './TagsConnector';

function TagSettings() {
  return (
    <PageContent title={translate('Tags')}>
      <SettingsToolbarConnector
        showSave={false}
      />

      <PageContentBody>
        <TagsConnector />
      </PageContentBody>
    </PageContent>
  );
}

export default TagSettings;
